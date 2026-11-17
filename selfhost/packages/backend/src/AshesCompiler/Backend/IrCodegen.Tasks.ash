// The task emitters for `AshesCompiler.Backend.IrCodegen` and the run-queue scheduler that drives
// tasks, over the task frame layout `TaskStructLayout` shares with lowering. Task values: a task
// already completed with its result (`CreateCompletedTask`), a coroutine task over a closure
// (`CreateTask`), a sleeping leaf (`AsyncSleep`), the `all`/`race` composites over a task list
// (`AsyncAll`, `AsyncRace`), task scopes (`scope`, `fork`, `join`), spawned tasks, and socket, TLS
// and HTTP leaves, each an arena frame laid out as stage 0 lays it out. `RunTask` runs stage 0's
// `ashes_scheduler_run`: tasks link into a FIFO run queue; a popped coroutine is stepped and, when it
// suspends, the task it awaits is scheduled with it as the waiter; a completed task hands its result
// to its waiter; a sleeping leaf parks until the earliest parked timer elapses; a composite
// schedules its children and completes when they have; a spawned task runs in a private arena
// installed around each of its steps. Of the socket leaves only the TCP connect is stepped, and a
// parked socket is polled rather than waited on with epoll; frame teardown and reclaiming a spawned
// task's arena are not ported.
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Arena
import AshesCompiler.Backend.IrCodegen.Syscalls.LinuxX64
import AshesCompiler.Backend.IrCodegen.Process.emitProcessPanicMessage
import AshesCompiler.Backend.IrCodegen.Filesystem.emitValidateUtf8
import AshesCompiler.Semantics.TaskStructLayout
import Ashes.Number.UInt
// `Ashes.Task.all(tasks)` and `Ashes.Task.race(tasks)`: a composite over the task list, scheduled
// when it is awaited.
// A leaf completes with `Error(message)`.
export (
    value emitCreateCompletedTask,
    value emitRunCompletedTask,
    type TaskRuntime(..),
    value emitCreateTask,
    value emitSleepTask,
    value emitCompositeTask,
    value emitSpawnTask,
    value emitCreateTaskScope,
    value emitCreateScopedTask,
    value emitForkScopedTask,
    value emitJoinScopedTask,
    value emitLeafTask,
    value defineTaskRuntime,
    value emitRunTask,
)

// The header word offsets the scheduler reads and writes.
let offsetArenaCursor =
    match taskStructLayout with
        | TaskStructLayout { arenaCursor = offset } -> offset

let offsetArenaEnd =
    match taskStructLayout with
        | TaskStructLayout { arenaEnd = offset } -> offset

let offsetArenaOwner =
    match taskStructLayout with
        | TaskStructLayout { arenaOwner = offset } -> offset

let offsetAwaitedTask =
    match taskStructLayout with
        | TaskStructLayout { awaitedTask = offset } -> offset

let offsetCoroutineFunction =
    match taskStructLayout with
        | TaskStructLayout { coroutineFunction = offset } -> offset

let offsetFrameSizeBytes =
    match taskStructLayout with
        | TaskStructLayout { frameSizeBytes = offset } -> offset

let offsetIoArgument0 =
    match taskStructLayout with
        | TaskStructLayout { ioArgument0 = offset } -> offset

let offsetIoArgument1 =
    match taskStructLayout with
        | TaskStructLayout { ioArgument1 = offset } -> offset

let offsetReadyNext =
    match taskStructLayout with
        | TaskStructLayout { readyNext = offset } -> offset

let offsetResultSlot =
    match taskStructLayout with
        | TaskStructLayout { resultSlot = offset } -> offset

let offsetSleepDurationMilliseconds =
    match taskStructLayout with
        | TaskStructLayout { sleepDurationMilliseconds = offset } -> offset

let offsetStateIndex =
    match taskStructLayout with
        | TaskStructLayout { stateIndex = offset } -> offset

let offsetWaitData0 =
    match taskStructLayout with
        | TaskStructLayout { waitData0 = offset } -> offset

let offsetWaitData1 =
    match taskStructLayout with
        | TaskStructLayout { waitData1 = offset } -> offset

let offsetWaiter =
    match taskStructLayout with
        | TaskStructLayout { waiter = offset } -> offset

let offsetWaitHandle =
    match taskStructLayout with
        | TaskStructLayout { waitHandle = offset } -> offset

let offsetWaitKind =
    match taskStructLayout with
        | TaskStructLayout { waitKind = offset } -> offset

let storeTaskWord builder i64 i8 ptrType taskRef offsetBytes value =
    "task_word"
    |> memOffsetPtr(builder)(i64)(i8)(ptrType)(taskRef)(offsetBytes)
    |> buildStore(builder)(value)

let recursive storeZeroTaskWords builder i64 i8 ptrType taskRef (offsets: List(Int)) =
    match offsets with
        | [] -> Unit
        | offset :: rest ->
            let _ =
                false
                |> constInt(i64)(0u64)
                |> storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(offset)
            in storeZeroTaskWords(builder)(i64)(i8)(ptrType)(taskRef)(rest)

let completedTaskZeroOffsets (layout: TaskStructLayout) =
    [
        layout.frameDropper,
        layout.coroutineFunction,
        layout.awaitedTask,
        layout.nextTask,
        layout.sleepDurationMilliseconds,
        layout.ioArgument0,
        layout.ioArgument1,
        layout.waitKind,
        layout.waitHandle,
        layout.waitData0,
        layout.waitData1,
        layout.arenaCursor,
        layout.arenaEnd,
        layout.readyNext,
        layout.waiter,
        layout.arenaOwner,
        layout.loopResetOk
    ]

let emitCreateCompletedTask context function_ builder i64 i8 ptrType (arena: ArenaRuntime) resultRef name =
    (let layout = taskStructLayout
    in
        let taskRef = emitArenaAlloc(context)(function_)(builder)(i64)(arena)(layout.headerSize)(name)
        in
            let _ =
                layout
                |> completedTaskZeroOffsets
                |> storeZeroTaskWords(builder)(i64)(i8)(ptrType)(taskRef)
            in
                let _ =
                    false
                    |> constInt(i64)(CompletedTaskState
                    |> taskStateCode
                    |> Ashes.Number.UInt.fromInt64)
                    |> storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(layout.stateIndex)
                in
                    let _ = storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(layout.resultSlot)(resultRef)
                    in
                        let _ =
                            false
                            |> constInt(i64)(Ashes.Number.UInt.fromInt64(layout.headerSize))
                            |> storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(layout.frameSizeBytes)
                        in taskRef)

let emitRunCompletedTask builder i64 i8 ptrType taskRef name =
    buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(taskRef)(offsetResultSlot)(name + "_result_ptr"))(name)

// A coroutine task: stage 0's `EmitCreateTask`. The frame holds the header, the closure's
// captures from `headerSize` on, and the words the state-machine transform saves across a
// suspend, which start cleared so a word the coroutine never reached reads as absent. The state
// starts at 0 and the coroutine function is the closure's code word.
let recursive copyTaskCaptures builder i64 i8 ptrType taskRef envRef (layout: TaskStructLayout) index count =
    if index >= count
    then Unit
    else
        let captured =
            buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(envRef)(index * 8)("task_capture_ptr"))("task_capture")
        in
            let _ = storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(layout.headerSize + index * 8)(captured)
            in copyTaskCaptures(builder)(i64)(i8)(ptrType)(taskRef)(envRef)(layout)(index + 1)(count)

let recursive frameWordOffsets offset size =
    if offset + 8 > size
    then []
    else offset :: frameWordOffsets(offset + 8)(size)

let emitCreateTask context function_ builder i64 i8 ptrType (arena: ArenaRuntime) closureRef stateStructSize captureCount name =
    (let layout = taskStructLayout
    in
        let taskRef = emitArenaAlloc(context)(function_)(builder)(i64)(arena)(stateStructSize)(name)
        in
            let _ = storeZeroTaskWords(builder)(i64)(i8)(ptrType)(taskRef)(layout.stateIndex :: layout.resultSlot :: completedTaskZeroOffsets(layout))
            in
                let codeWord =
                    buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(closureRef)(0)(name + "_code_ptr"))(name + "_code")
                in
                    let _ = storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(layout.coroutineFunction)(codeWord)
                    in
                        let _ =
                            false
                            |> constInt(i64)(Ashes.Number.UInt.fromInt64(stateStructSize))
                            |> storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(layout.frameSizeBytes)
                        in
                            let _ =
                                stateStructSize
                                |> frameWordOffsets(layout.headerSize + captureCount * 8)
                                |> storeZeroTaskWords(builder)(i64)(i8)(ptrType)(taskRef)
                            in
                                let envRef =
                                    buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(closureRef)(8)(name + "_env_ptr"))(name + "_env")
                                in
                                    let _ = copyTaskCaptures(builder)(i64)(i8)(ptrType)(taskRef)(envRef)(layout)(0)(captureCount)
                                    in taskRef)

let recursive storeTaskWords builder i64 i8 ptrType taskRef (words: List((Int, LLVMValueRef))) =
    match words with
        | [] -> Unit
        | (offset, value) :: rest ->
            let _ = storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(offset)(value)
            in storeTaskWords(builder)(i64)(i8)(ptrType)(taskRef)(rest)

let wordConst i64 (value: Int) =
    constInt(i64)(Ashes.Number.UInt.fromInt64(value))(false)

let loadTaskWord builder i64 i8 ptrType taskRef offsetBytes name =
    buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(taskRef)(offsetBytes)(name + "_ptr"))(name)

let stateConst i64 (kind: TaskStateKind) =
    kind
    |> taskStateCode
    |> wordConst(i64)

// A header-only task frame in the arena, every word zero but its state and the given words.
let emitHeaderTask context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (stateCode: Int) (words: List((Int, LLVMValueRef))) name =
    (let layout = taskStructLayout
    in
        let taskRef = emitArenaAlloc(context)(function_)(builder)(i64)(arena)(layout.headerSize)(name)
        in
            let _ = storeZeroTaskWords(builder)(i64)(i8)(ptrType)(taskRef)(layout.resultSlot :: completedTaskZeroOffsets(layout))
            in
                let _ =
                    stateCode
                    |> wordConst(i64)
                    |> storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(layout.stateIndex)
                in
                    let _ =
                        layout.headerSize
                        |> wordConst(i64)
                        |> storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(layout.frameSizeBytes)
                    in
                        let _ = storeTaskWords(builder)(i64)(i8)(ptrType)(taskRef)(words)
                        in taskRef)

// `Ashes.Task.sleep(ms)`: a sleeping leaf whose remaining milliseconds the scheduler counts down.
let emitSleepTask context function_ builder i64 i8 ptrType (arena: ArenaRuntime) millisecondsRef name =
    emitHeaderTask(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(taskStateCode(SleepingTaskState))([(offsetSleepDurationMilliseconds, millisecondsRef)])(name)

let emitCompositeTask context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (kind: TaskStateKind) listRef name =
    emitHeaderTask(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(taskStateCode(kind))([(offsetIoArgument0, listRef)])(name)

// A socket, TLS or HTTP leaf task, stage 0's `EmitCreateLeafNetworkingTask`: a header frame with the
// leaf's state code and its two operands; the TLS server handshake keeps its key in `WaitData1`.
let emitLeafTask context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (kind: TaskStateKind) firstRef secondRef thirdRef name =
    emitHeaderTask(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(taskStateCode(kind))([(offsetIoArgument0, firstRef), (offsetIoArgument1, secondRef), (offsetWaitData1, thirdRef)])(name)

type TaskRuntime =
    | runTaskFn: LLVMValueRef
    | runTaskType: LLVMTypeRef
    | spawnEnqueueFn: LLVMValueRef
    | spawnEnqueueType: LLVMTypeRef
    | forkScopedFn: LLVMValueRef
    | forkScopedType: LLVMTypeRef

type SchedulerGlobals =
    | readyQueueHead: LLVMValueRef
    | readyQueueTail: LLVMValueRef
    | parkedLeafHead: LLVMValueRef
    | schedulerRunDepth: LLVMValueRef
    | epollDescriptor: LLVMValueRef

// The run queue's `ashes_ready_enqueue`: the task appended at the tail, its link cleared.
let defineReadyEnqueue module_ context builder i64 i8 ptrType (globals: SchedulerGlobals) =
    (let enqueueType = functionType(i64)([i64])(1u32)(false)
    in
        let enqueueFn = addInternalFunction(module_)("__ashes_ready_enqueue")(enqueueType)
        in
            let entryBlock = appendBasicBlock(context)(enqueueFn)("entry")
            in
                let emptyBlock = appendBasicBlock(context)(enqueueFn)("enqueue_empty")
                in
                    let appendBlock = appendBasicBlock(context)(enqueueFn)("enqueue_append")
                    in
                        let doneBlock = appendBasicBlock(context)(enqueueFn)("enqueue_done")
                        in
                            let task = getParam(enqueueFn)(0u32)
                            in
                                let _ = positionBuilderAtEnd(builder)(entryBlock)
                                in
                                    let _ =
                                        0
                                        |> wordConst(i64)
                                        |> storeTaskWord(builder)(i64)(i8)(ptrType)(task)(offsetReadyNext)
                                    in
                                        let tail = buildLoad(builder)(i64)(globals.readyQueueTail)("enqueue_tail")
                                        in
                                            let _ =
                                                buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(tail)(wordConst(i64)(0))("enqueue_is_empty"))(emptyBlock)(appendBlock)
                                            in
                                                let _ = positionBuilderAtEnd(builder)(emptyBlock)
                                                in
                                                    let _ = buildStore(builder)(task)(globals.readyQueueHead)
                                                    in
                                                        let _ = buildBr(builder)(doneBlock)
                                                        in
                                                            let _ = positionBuilderAtEnd(builder)(appendBlock)
                                                            in
                                                                let _ = storeTaskWord(builder)(i64)(i8)(ptrType)(tail)(offsetReadyNext)(task)
                                                                in
                                                                    let _ = buildBr(builder)(doneBlock)
                                                                    in
                                                                        let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                        in
                                                                            let _ = buildStore(builder)(task)(globals.readyQueueTail)
                                                                            in
                                                                                let _ =
                                                                                    0
                                                                                    |> wordConst(i64)
                                                                                    |> buildRet(builder)
                                                                                in (enqueueFn, enqueueType))

// The run queue's `ashes_ready_dequeue`: the head task, or 0 when the queue is empty.
let defineReadyDequeue module_ context builder i64 i8 ptrType (globals: SchedulerGlobals) =
    (let dequeueType = functionType(i64)([])(0u32)(false)
    in
        let dequeueFn = addInternalFunction(module_)("__ashes_ready_dequeue")(dequeueType)
        in
            let entryBlock = appendBasicBlock(context)(dequeueFn)("entry")
            in
                let emptyBlock = appendBasicBlock(context)(dequeueFn)("dequeue_empty")
                in
                    let popBlock = appendBasicBlock(context)(dequeueFn)("dequeue_pop")
                    in
                        let lastBlock = appendBasicBlock(context)(dequeueFn)("dequeue_last")
                        in
                            let doneBlock = appendBasicBlock(context)(dequeueFn)("dequeue_done")
                            in
                                let _ = positionBuilderAtEnd(builder)(entryBlock)
                                in
                                    let head = buildLoad(builder)(i64)(globals.readyQueueHead)("dequeue_head")
                                    in
                                        let _ =
                                            buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(head)(wordConst(i64)(0))("dequeue_is_empty"))(emptyBlock)(popBlock)
                                        in
                                            let _ = positionBuilderAtEnd(builder)(emptyBlock)
                                            in
                                                let _ =
                                                    0
                                                    |> wordConst(i64)
                                                    |> buildRet(builder)
                                                in
                                                    let _ = positionBuilderAtEnd(builder)(popBlock)
                                                    in
                                                        let next = loadTaskWord(builder)(i64)(i8)(ptrType)(head)(offsetReadyNext)("dequeue_next")
                                                        in
                                                            let _ = buildStore(builder)(next)(globals.readyQueueHead)
                                                            in
                                                                let _ =
                                                                    buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(next)(wordConst(i64)(0))("dequeue_now_empty"))(lastBlock)(doneBlock)
                                                                in
                                                                    let _ = positionBuilderAtEnd(builder)(lastBlock)
                                                                    in
                                                                        let _ =
                                                                            buildStore(builder)(wordConst(i64)(0))(globals.readyQueueTail)
                                                                        in
                                                                            let _ = buildBr(builder)(doneBlock)
                                                                            in
                                                                                let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                in
                                                                                    let _ =
                                                                                        0
                                                                                        |> wordConst(i64)
                                                                                        |> storeTaskWord(builder)(i64)(i8)(ptrType)(head)(offsetReadyNext)
                                                                                    in
                                                                                        let _ = buildRet(builder)(head)
                                                                                        in (dequeueFn, dequeueType))

type SchedulerContext =
    | schedContext: LLVMContextRef
    | schedBuilder: LLVMBuilderRef
    | schedI64: LLVMTypeRef
    | schedI8: LLVMTypeRef
    | schedPtr: LLVMTypeRef
    | schedArena: ArenaRuntime
    | schedGlobals: SchedulerGlobals
    | enqueueFn: LLVMValueRef
    | enqueueType: LLVMTypeRef

let readyHeadGlobal (sc: SchedulerContext) =
    match sc.schedGlobals with
        | SchedulerGlobals { readyQueueHead = global } -> global

let readyTailGlobal (sc: SchedulerContext) =
    match sc.schedGlobals with
        | SchedulerGlobals { readyQueueTail = global } -> global

let parkedHeadGlobal (sc: SchedulerContext) =
    match sc.schedGlobals with
        | SchedulerGlobals { parkedLeafHead = global } -> global

let depthGlobal (sc: SchedulerContext) =
    match sc.schedGlobals with
        | SchedulerGlobals { schedulerRunDepth = global } -> global

let epollGlobal (sc: SchedulerContext) =
    match sc.schedGlobals with
        | SchedulerGlobals { epollDescriptor = global } -> global

let schedWord (sc: SchedulerContext) taskRef offsetBytes name = loadTaskWord(sc.schedBuilder)(sc.schedI64)(sc.schedI8)(sc.schedPtr)(taskRef)(offsetBytes)(name)

let schedStore (sc: SchedulerContext) taskRef offsetBytes value = storeTaskWord(sc.schedBuilder)(sc.schedI64)(sc.schedI8)(sc.schedPtr)(taskRef)(offsetBytes)(value)

let schedConst (sc: SchedulerContext) (value: Int) = wordConst(sc.schedI64)(value)

let schedEnqueue (sc: SchedulerContext) taskRef = buildCall(sc.schedBuilder)(sc.enqueueType)(sc.enqueueFn)([taskRef])(1u32)("")

let schedAt (sc: SchedulerContext) block = positionBuilderAtEnd(sc.schedBuilder)(block)

let schedBr (sc: SchedulerContext) block = buildBr(sc.schedBuilder)(block)

let schedIf (sc: SchedulerContext) condition thenBlock elseBlock = buildCondBr(sc.schedBuilder)(condition)(thenBlock)(elseBlock)

let schedEq (sc: SchedulerContext) left right name = buildICmp(sc.schedBuilder)(intPredicateEq)(left)(right)(name)

let schedSlot (sc: SchedulerContext) name = buildAlloca(sc.schedBuilder)(sc.schedI64)(name)

let schedLoad (sc: SchedulerContext) slot name = buildLoad(sc.schedBuilder)(sc.schedI64)(slot)(name)

let schedSet (sc: SchedulerContext) slot value = buildStore(sc.schedBuilder)(value)(slot)

// `Ok(value)` as a fresh arena cell, `[tag 0][value]`.
let schedOk (sc: SchedulerContext) function_ value name =
    (let cell = emitArenaAlloc(sc.schedContext)(function_)(sc.schedBuilder)(sc.schedI64)(sc.schedArena)(16)(name)
    in
        let _ =
            0
            |> schedConst(sc)
            |> schedStore(sc)(cell)(0)
        in
            let _ = schedStore(sc)(cell)(8)(value)
            in cell)

// A cons cell `[head][tail]` in the arena.
let schedCons (sc: SchedulerContext) function_ head tail name =
    (let cell = emitArenaAlloc(sc.schedContext)(function_)(sc.schedBuilder)(sc.schedI64)(sc.schedArena)(16)(name)
    in
        let _ = schedStore(sc)(cell)(0)(head)
        in
            let _ = schedStore(sc)(cell)(8)(tail)
            in cell)

let arenaCursorOf (sc: SchedulerContext) =
    match sc.schedArena with
        | ArenaRuntime { arenaCursorGlobal = global } -> global

let arenaEndOf (sc: SchedulerContext) =
    match sc.schedArena with
        | ArenaRuntime { arenaEndGlobal = global } -> global

// Emits one step of `task` with its owner's arena as the allocator, stage 0's
// `EmitInstallTaskArena` and `EmitRestoreTaskArena`: a task with an owner (a spawned task, or one
// such a task awaits) allocates in the owner's private arena, whose grown cursor and end are written
// back after the step; a task with no owner allocates in the arena of the run.
let schedWithTaskArena (sc: SchedulerContext) runFn task prefix emit =
    (let owner = schedWord(sc)(task)(offsetArenaOwner)(prefix + "_owner")
    in
        let savedCursor =
            schedLoad(sc)(arenaCursorOf(sc))(prefix + "_saved_cursor")
        in
            let savedEnd =
                schedLoad(sc)(arenaEndOf(sc))(prefix + "_saved_end")
            in
                let installBlock = appendBasicBlock(sc.schedContext)(runFn)(prefix + "_install")
                in
                    let installedBlock = appendBasicBlock(sc.schedContext)(runFn)(prefix + "_installed")
                    in
                        let hasOwner =
                            buildICmp(sc.schedBuilder)(intPredicateNe)(owner)(schedConst(sc)(0))(prefix + "_has_owner")
                        in
                            let _ = schedIf(sc)(hasOwner)(installBlock)(installedBlock)
                            in
                                let _ = schedAt(sc)(installBlock)
                                in
                                    let _ =
                                        prefix + "_owner_cursor"
                                        |> schedWord(sc)(owner)(offsetArenaCursor)
                                        |> schedSet(sc)(arenaCursorOf(sc))
                                    in
                                        let _ =
                                            prefix + "_owner_end"
                                            |> schedWord(sc)(owner)(offsetArenaEnd)
                                            |> schedSet(sc)(arenaEndOf(sc))
                                        in
                                            let _ = schedBr(sc)(installedBlock)
                                            in
                                                let _ = schedAt(sc)(installedBlock)
                                                in
                                                    let result = emit(Unit)
                                                    in
                                                        let writeBackBlock = appendBasicBlock(sc.schedContext)(runFn)(prefix + "_write_back")
                                                        in
                                                            let restoredBlock = appendBasicBlock(sc.schedContext)(runFn)(prefix + "_restored")
                                                            in
                                                                let _ = schedIf(sc)(hasOwner)(writeBackBlock)(restoredBlock)
                                                                in
                                                                    let _ = schedAt(sc)(writeBackBlock)
                                                                    in
                                                                        let _ =
                                                                            prefix + "_grown_cursor"
                                                                            |> schedLoad(sc)(arenaCursorOf(sc))
                                                                            |> schedStore(sc)(owner)(offsetArenaCursor)
                                                                        in
                                                                            let _ =
                                                                                prefix + "_grown_end"
                                                                                |> schedLoad(sc)(arenaEndOf(sc))
                                                                                |> schedStore(sc)(owner)(offsetArenaEnd)
                                                                            in
                                                                                let _ =
                                                                                    schedSet(sc)(arenaCursorOf(sc))(savedCursor)
                                                                                in
                                                                                    let _ =
                                                                                        schedSet(sc)(arenaEndOf(sc))(savedEnd)
                                                                                    in
                                                                                        let _ = schedBr(sc)(restoredBlock)
                                                                                        in
                                                                                            let _ = schedAt(sc)(restoredBlock)
                                                                                            in result)

// A cancelled scope composite cancels every child it forked, in fork order (a fork node is
// `[child][joined][next]`), so cancellation reaches into nested scopes. Leaves the builder in the
// block after the children.
let emitCancelScopeChildren (sc: SchedulerContext) cancelFn cancelType task cursorSlot =
    (let initBlock = appendBasicBlock(sc.schedContext)(cancelFn)("cancel_scope_init")
    in
        let loopBlock = appendBasicBlock(sc.schedContext)(cancelFn)("cancel_scope_loop")
        in
            let childBlock = appendBasicBlock(sc.schedContext)(cancelFn)("cancel_scope_child")
            in
                let afterBlock = appendBasicBlock(sc.schedContext)(cancelFn)("cancel_scope_done")
                in
                    let isScope =
                        schedEq(sc)(schedWord(sc)(task)(offsetStateIndex)("cancel_scope_state"))(stateConst(sc.schedI64)(ScopeCompositeTaskState))("cancel_is_scope")
                    in
                        let _ = schedIf(sc)(isScope)(initBlock)(afterBlock)
                        in
                            let _ = schedAt(sc)(initBlock)
                            in
                                let _ =
                                    "cancel_scope_head"
                                    |> schedWord(sc)(schedWord(sc)(task)(offsetIoArgument1)("cancel_scope_token"))(0)
                                    |> schedSet(sc)(cursorSlot)
                                in
                                    let _ = schedBr(sc)(loopBlock)
                                    in
                                        let _ = schedAt(sc)(loopBlock)
                                        in
                                            let node = schedLoad(sc)(cursorSlot)("cancel_scope_node")
                                            in
                                                let _ =
                                                    schedIf(sc)(schedEq(sc)(node)(schedConst(sc)(0))("cancel_scope_end"))(afterBlock)(childBlock)
                                                in
                                                    let _ = schedAt(sc)(childBlock)
                                                    in
                                                        let _ = buildCall(sc.schedBuilder)(cancelType)(cancelFn)([schedWord(sc)(node)(0)("cancel_scope_child_task")])(1u32)("")
                                                        in
                                                            let _ =
                                                                "cancel_scope_next"
                                                                |> schedWord(sc)(node)(16)
                                                                |> schedSet(sc)(cursorSlot)
                                                            in
                                                                let _ = schedBr(sc)(loopBlock)
                                                                in schedAt(sc)(afterBlock))

// Stage 0's `ashes_cancel_task`, without the socket close (no socket leaf is ported): an unfinished
// task and the task it awaits complete with `Ok(0)`, so a race loser drops out of the run.
let defineCancelTask module_ (sc: SchedulerContext) =
    (let cancelType = functionType(sc.schedI64)([sc.schedI64])(1u32)(false)
    in
        let cancelFn = addInternalFunction(module_)("__ashes_cancel_task")(cancelType)
        in
            let entryBlock = appendBasicBlock(sc.schedContext)(cancelFn)("entry")
            in
                let workBlock = appendBasicBlock(sc.schedContext)(cancelFn)("cancel_work")
                in
                    let awaitedBlock = appendBasicBlock(sc.schedContext)(cancelFn)("cancel_awaited")
                    in
                        let finishBlock = appendBasicBlock(sc.schedContext)(cancelFn)("cancel_finish")
                        in
                            let doneBlock = appendBasicBlock(sc.schedContext)(cancelFn)("cancel_done")
                            in
                                let task = getParam(cancelFn)(0u32)
                                in
                                    let _ = schedAt(sc)(entryBlock)
                                    in
                                        let cursorSlot = schedSlot(sc)("cancel_scope_cursor")
                                        in
                                            let _ =
                                                schedIf(sc)(schedEq(sc)(schedWord(sc)(task)(offsetStateIndex)("cancel_state"))(stateConst(sc.schedI64)(CompletedTaskState))("cancel_already_done"))(doneBlock)(workBlock)
                                            in
                                                let _ = schedAt(sc)(workBlock)
                                                in
                                                    let _ = emitCancelScopeChildren(sc)(cancelFn)(cancelType)(task)(cursorSlot)
                                                    in
                                                        let awaited = schedWord(sc)(task)(offsetAwaitedTask)("cancel_awaited_task")
                                                        in
                                                            let _ =
                                                                schedIf(sc)(schedEq(sc)(awaited)(schedConst(sc)(0))("cancel_no_awaited"))(finishBlock)(awaitedBlock)
                                                            in
                                                                let _ = schedAt(sc)(awaitedBlock)
                                                                in
                                                                    let _ = buildCall(sc.schedBuilder)(cancelType)(cancelFn)([awaited])(1u32)("cancel_recurse")
                                                                    in
                                                                        let _ =
                                                                            0
                                                                            |> schedConst(sc)
                                                                            |> schedStore(sc)(task)(offsetAwaitedTask)
                                                                        in
                                                                            let _ = schedBr(sc)(finishBlock)
                                                                            in
                                                                                let _ = schedAt(sc)(finishBlock)
                                                                                in
                                                                                    let _ =
                                                                                        "cancel_result"
                                                                                        |> schedOk(sc)(cancelFn)(schedConst(sc)(0))
                                                                                        |> schedStore(sc)(task)(offsetResultSlot)
                                                                                    in
                                                                                        let _ =
                                                                                            CompletedTaskState
                                                                                            |> stateConst(sc.schedI64)
                                                                                            |> schedStore(sc)(task)(offsetStateIndex)
                                                                                        in
                                                                                            let _ = schedBr(sc)(doneBlock)
                                                                                            in
                                                                                                let _ = schedAt(sc)(doneBlock)
                                                                                                in
                                                                                                    let _ =
                                                                                                        0
                                                                                                        |> schedConst(sc)
                                                                                                        |> buildRet(sc.schedBuilder)
                                                                                                    in (cancelFn, cancelType))

// The successful collection of `all`: the children's `Ok` values consed onto an accumulator in
// list order, which leaves them reversed, then reversed back, and the composite completed with
// `Ok` of that list.
let emitCompositeCollect (sc: SchedulerContext) stepFn task children cursorSlot listSlot buildInit buildBody reverseInit reverseBody succeededBlock completeBlock stepType =
    (let buildCheck = appendBasicBlock(sc.schedContext)(stepFn)("composite_build_check")
    in
        let reverseCheck = appendBasicBlock(sc.schedContext)(stepFn)("composite_reverse_check")
        in
            let _ = schedSet(sc)(cursorSlot)(children)
            in
                let _ =
                    0
                    |> schedConst(sc)
                    |> schedSet(sc)(listSlot)
                in
                    let _ = schedBr(sc)(buildCheck)
                    in
                        let _ = schedAt(sc)(buildCheck)
                        in
                            let buildCursor = schedLoad(sc)(cursorSlot)("composite_build_cursor")
                            in
                                let _ =
                                    schedIf(sc)(schedEq(sc)(buildCursor)(schedConst(sc)(0))("composite_build_end"))(reverseInit)(buildBody)
                                in
                                    let _ = schedAt(sc)(buildBody)
                                    in
                                        let childResult =
                                            schedWord(sc)(schedWord(sc)(buildCursor)(0)("composite_build_child"))(offsetResultSlot)("composite_build_result")
                                        in
                                            let _ =
                                                "composite_build_tail"
                                                |> schedWord(sc)(buildCursor)(8)
                                                |> schedSet(sc)(cursorSlot)
                                            in
                                                let _ =
                                                    "composite_build_cell"
                                                    |> schedCons(sc)(stepFn)(schedWord(sc)(childResult)(8)("composite_build_value"))(schedLoad(sc)(listSlot)("composite_build_acc"))
                                                    |> schedSet(sc)(listSlot)
                                                in
                                                    let _ = schedBr(sc)(buildCheck)
                                                    in
                                                        let _ = schedAt(sc)(reverseInit)
                                                        in
                                                            let _ =
                                                                "composite_reversed"
                                                                |> schedLoad(sc)(listSlot)
                                                                |> schedSet(sc)(cursorSlot)
                                                            in
                                                                let _ =
                                                                    0
                                                                    |> schedConst(sc)
                                                                    |> schedSet(sc)(listSlot)
                                                                in
                                                                    let _ = schedBr(sc)(reverseCheck)
                                                                    in
                                                                        let _ = schedAt(sc)(reverseCheck)
                                                                        in
                                                                            let reverseCursor = schedLoad(sc)(cursorSlot)("composite_reverse_cursor")
                                                                            in
                                                                                let _ =
                                                                                    schedIf(sc)(schedEq(sc)(reverseCursor)(schedConst(sc)(0))("composite_reverse_end"))(succeededBlock)(reverseBody)
                                                                                in
                                                                                    let _ = schedAt(sc)(reverseBody)
                                                                                    in
                                                                                        let _ =
                                                                                            "composite_reverse_tail"
                                                                                            |> schedWord(sc)(reverseCursor)(8)
                                                                                            |> schedSet(sc)(cursorSlot)
                                                                                        in
                                                                                            let _ =
                                                                                                "composite_reverse_cell"
                                                                                                |> schedCons(sc)(stepFn)(schedWord(sc)(reverseCursor)(0)("composite_reverse_head"))(schedLoad(sc)(listSlot)("composite_reverse_acc"))
                                                                                                |> schedSet(sc)(listSlot)
                                                                                            in
                                                                                                let _ = schedBr(sc)(reverseCheck)
                                                                                                in
                                                                                                    let _ = schedAt(sc)(succeededBlock)
                                                                                                    in
                                                                                                        let _ =
                                                                                                            "composite_ok"
                                                                                                            |> schedOk(sc)(stepFn)(schedLoad(sc)(listSlot)("composite_values"))
                                                                                                            |> schedStore(sc)(task)(offsetResultSlot)
                                                                                                        in
                                                                                                            let _ = schedBr(sc)(completeBlock)
                                                                                                            in
                                                                                                                let _ = schedAt(sc)(completeBlock)
                                                                                                                in
                                                                                                                    let _ =
                                                                                                                        CompletedTaskState
                                                                                                                        |> stateConst(sc.schedI64)
                                                                                                                        |> schedStore(sc)(task)(offsetStateIndex)
                                                                                                                    in
                                                                                                                        let _ =
                                                                                                                            1
                                                                                                                            |> schedConst(sc)
                                                                                                                            |> buildRet(sc.schedBuilder)
                                                                                                                        in (stepFn, stepType))

// Stage 0's `EmitStepScopeComposite`. The first step schedules the scoped parent task with the
// composite as its waiter. The step after the parent completes cancels every child still running,
// in fork order, and completes with the parent's result unless an unjoined child failed while the
// parent succeeded, in which case the first such failure wins; the scope is then closed.
let defineStepScope module_ (sc: SchedulerContext) cancelFn cancelType =
    (let stepType = functionType(sc.schedI64)([sc.schedI64])(1u32)(false)
    in
        let stepFn = addInternalFunction(module_)("__ashes_step_scope")(stepType)
        in
            let block =
                given (name) -> appendBasicBlock(sc.schedContext)(stepFn)(name)
            in
                let entryBlock = block("entry")
                in
                    let startBlock = block("scope_start")
                    in
                        let finishBlock = block("scope_finish")
                        in
                            let loopBlock = block("scope_loop")
                            in
                                let childBlock = block("scope_child")
                                in
                                    let cancelBlock = block("scope_cancel")
                                    in
                                        let inspectBlock = block("scope_inspect")
                                        in
                                            let replaceBlock = block("scope_replace")
                                            in
                                                let nextBlock = block("scope_next")
                                                in
                                                    let completeBlock = block("scope_complete")
                                                    in
                                                        let task = getParam(stepFn)(0u32)
                                                        in
                                                            let _ = schedAt(sc)(entryBlock)
                                                            in
                                                                let resultSlot = schedSlot(sc)("scope_result")
                                                                in
                                                                    let cursorSlot = schedSlot(sc)("scope_cursor")
                                                                    in
                                                                        let _ =
                                                                            schedIf(sc)(schedEq(sc)(schedWord(sc)(task)(offsetWaitData0)("scope_phase"))(schedConst(sc)(0))("scope_is_start"))(startBlock)(finishBlock)
                                                                        in
                                                                            let _ = schedAt(sc)(startBlock)
                                                                            in
                                                                                let _ =
                                                                                    1
                                                                                    |> schedConst(sc)
                                                                                    |> schedStore(sc)(task)(offsetWaitData0)
                                                                                in
                                                                                    let parent = schedWord(sc)(task)(offsetAwaitedTask)("scope_parent")
                                                                                    in
                                                                                        let _ = schedStore(sc)(parent)(offsetWaiter)(task)
                                                                                        in
                                                                                            let _ =
                                                                                                "scope_owner"
                                                                                                |> schedWord(sc)(task)(offsetArenaOwner)
                                                                                                |> schedStore(sc)(parent)(offsetArenaOwner)
                                                                                            in
                                                                                                let _ = schedEnqueue(sc)(parent)
                                                                                                in
                                                                                                    let _ =
                                                                                                        0
                                                                                                        |> schedConst(sc)
                                                                                                        |> buildRet(sc.schedBuilder)
                                                                                                    in
                                                                                                        let _ = schedAt(sc)(finishBlock)
                                                                                                        in
                                                                                                            let _ =
                                                                                                                "scope_parent_result"
                                                                                                                |> schedWord(sc)(task)(offsetResultSlot)
                                                                                                                |> schedSet(sc)(resultSlot)
                                                                                                            in
                                                                                                                let scope = schedWord(sc)(task)(offsetIoArgument1)("scope_token")
                                                                                                                in
                                                                                                                    let _ =
                                                                                                                        "scope_head"
                                                                                                                        |> schedWord(sc)(scope)(0)
                                                                                                                        |> schedSet(sc)(cursorSlot)
                                                                                                                    in
                                                                                                                        let _ = schedBr(sc)(loopBlock)
                                                                                                                        in
                                                                                                                            let _ = schedAt(sc)(loopBlock)
                                                                                                                            in
                                                                                                                                let node = schedLoad(sc)(cursorSlot)("scope_node")
                                                                                                                                in
                                                                                                                                    let _ =
                                                                                                                                        schedIf(sc)(schedEq(sc)(node)(schedConst(sc)(0))("scope_children_done"))(completeBlock)(childBlock)
                                                                                                                                    in
                                                                                                                                        let _ = schedAt(sc)(childBlock)
                                                                                                                                        in
                                                                                                                                            let child = schedWord(sc)(node)(0)("scope_child_task")
                                                                                                                                            in
                                                                                                                                                let _ =
                                                                                                                                                    schedIf(sc)(schedEq(sc)(schedWord(sc)(child)(offsetStateIndex)("scope_child_state"))(stateConst(sc.schedI64)(CompletedTaskState))("scope_child_done"))(inspectBlock)(cancelBlock)
                                                                                                                                                in
                                                                                                                                                    let _ = schedAt(sc)(cancelBlock)
                                                                                                                                                    in
                                                                                                                                                        let _ = buildCall(sc.schedBuilder)(cancelType)(cancelFn)([child])(1u32)("")
                                                                                                                                                        in
                                                                                                                                                            let _ = schedBr(sc)(inspectBlock)
                                                                                                                                                            in
                                                                                                                                                                let _ = schedAt(sc)(inspectBlock)
                                                                                                                                                                in
                                                                                                                                                                    let childResult = schedWord(sc)(child)(offsetResultSlot)("scope_child_result")
                                                                                                                                                                    in
                                                                                                                                                                        let unjoined =
                                                                                                                                                                            schedEq(sc)(schedWord(sc)(node)(8)("scope_joined"))(schedConst(sc)(0))("scope_unjoined")
                                                                                                                                                                        in
                                                                                                                                                                            let childFailed =
                                                                                                                                                                                buildICmp(sc.schedBuilder)(intPredicateNe)(schedWord(sc)(childResult)(0)("scope_child_tag"))(schedConst(sc)(0))("scope_child_failed")
                                                                                                                                                                            in
                                                                                                                                                                                let currentOk =
                                                                                                                                                                                    schedEq(sc)(schedWord(sc)(schedLoad(sc)(resultSlot)("scope_current"))(0)("scope_current_tag"))(schedConst(sc)(0))("scope_current_ok")
                                                                                                                                                                                in
                                                                                                                                                                                    let _ =
                                                                                                                                                                                        schedIf(sc)(buildAnd(sc.schedBuilder)(unjoined)(buildAnd(sc.schedBuilder)(childFailed)(currentOk)("scope_failure_wins"))("scope_replace_result"))(replaceBlock)(nextBlock)
                                                                                                                                                                                    in
                                                                                                                                                                                        let _ = schedAt(sc)(replaceBlock)
                                                                                                                                                                                        in
                                                                                                                                                                                            let _ = schedSet(sc)(resultSlot)(childResult)
                                                                                                                                                                                            in
                                                                                                                                                                                                let _ = schedBr(sc)(nextBlock)
                                                                                                                                                                                                in
                                                                                                                                                                                                    let _ = schedAt(sc)(nextBlock)
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                            "scope_next_node"
                                                                                                                                                                                                            |> schedWord(sc)(node)(16)
                                                                                                                                                                                                            |> schedSet(sc)(cursorSlot)
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let _ = schedBr(sc)(loopBlock)
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let _ = schedAt(sc)(completeBlock)
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                        1
                                                                                                                                                                                                                        |> schedConst(sc)
                                                                                                                                                                                                                        |> schedStore(sc)(scope)(24)
                                                                                                                                                                                                                    in
                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                            "scope_final_result"
                                                                                                                                                                                                                            |> schedLoad(sc)(resultSlot)
                                                                                                                                                                                                                            |> schedStore(sc)(task)(offsetResultSlot)
                                                                                                                                                                                                                        in
                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                CompletedTaskState
                                                                                                                                                                                                                                |> stateConst(sc.schedI64)
                                                                                                                                                                                                                                |> schedStore(sc)(task)(offsetStateIndex)
                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                    1
                                                                                                                                                                                                                                    |> schedConst(sc)
                                                                                                                                                                                                                                    |> buildRet(sc.schedBuilder)
                                                                                                                                                                                                                                in (stepFn, stepType))

// Stage 0's `EmitForkScopedTask`: the child joins the task scope its owning coroutine runs under
// (the nearest explicit `Ashes.Task.scope` above the coroutine's own implicit scope, else that
// implicit scope) as a fork node `[child][joined][next]`, is scheduled with no waiter, and the
// fork answers a completed task holding `Ok(node)`, the join handle.
let defineForkScoped module_ (sc: SchedulerContext) =
    (let forkType = functionType(sc.schedI64)([sc.schedI64, sc.schedI64])(2u32)(false)
    in
        let forkFn = addInternalFunction(module_)("__ashes_fork_scoped")(forkType)
        in
            let block =
                given (name) -> appendBasicBlock(sc.schedContext)(forkFn)(name)
            in
                let entryBlock = block("entry")
                in
                    let checkBlock = block("fork_scope_check")
                    in
                        let outerBlock = block("fork_scope_outer")
                        in
                            let candidateBlock = block("fork_scope_candidate")
                            in
                                let selectBlock = block("fork_scope_select")
                                in
                                    let nextBlock = block("fork_scope_next")
                                    in
                                        let readyBlock = block("fork_scope_ready")
                                        in
                                            let firstBlock = block("fork_first")
                                            in
                                                let appendBlock = block("fork_append")
                                                in
                                                    let linkedBlock = block("fork_linked")
                                                    in
                                                        let owner = getParam(forkFn)(0u32)
                                                        in
                                                            let child = getParam(forkFn)(1u32)
                                                            in
                                                                let _ = schedAt(sc)(entryBlock)
                                                                in
                                                                    let compositeSlot = schedSlot(sc)("fork_composite_cursor")
                                                                    in
                                                                        let scopeSlot = schedSlot(sc)("fork_selected_scope")
                                                                        in
                                                                            let composite = schedWord(sc)(owner)(offsetWaiter)("fork_composite")
                                                                            in
                                                                                let nearest = schedWord(sc)(composite)(offsetIoArgument1)("fork_nearest_scope")
                                                                                in
                                                                                    let _ = schedSet(sc)(compositeSlot)(composite)
                                                                                    in
                                                                                        let _ = schedSet(sc)(scopeSlot)(nearest)
                                                                                        in
                                                                                            let _ =
                                                                                                schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateNe)(schedWord(sc)(nearest)(32)("fork_nearest_explicit"))(schedConst(sc)(0))("fork_nearest_is_explicit"))(readyBlock)(checkBlock)
                                                                                            in
                                                                                                let _ = schedAt(sc)(checkBlock)
                                                                                                in
                                                                                                    let outer =
                                                                                                        schedWord(sc)(schedLoad(sc)(compositeSlot)("fork_scope_cursor"))(offsetWaiter)("fork_outer_composite")
                                                                                                    in
                                                                                                        let _ =
                                                                                                            schedIf(sc)(schedEq(sc)(outer)(schedConst(sc)(0))("fork_no_outer_scope"))(readyBlock)(outerBlock)
                                                                                                        in
                                                                                                            let _ = schedAt(sc)(outerBlock)
                                                                                                            in
                                                                                                                let _ =
                                                                                                                    schedIf(sc)(schedEq(sc)(schedWord(sc)(outer)(offsetStateIndex)("fork_outer_state"))(stateConst(sc.schedI64)(ScopeCompositeTaskState))("fork_outer_is_scope"))(candidateBlock)(readyBlock)
                                                                                                                in
                                                                                                                    let _ = schedAt(sc)(candidateBlock)
                                                                                                                    in
                                                                                                                        let outerScope = schedWord(sc)(outer)(offsetIoArgument1)("fork_outer_scope")
                                                                                                                        in
                                                                                                                            let _ =
                                                                                                                                schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateNe)(schedWord(sc)(outerScope)(32)("fork_outer_explicit"))(schedConst(sc)(0))("fork_outer_is_explicit"))(selectBlock)(nextBlock)
                                                                                                                            in
                                                                                                                                let _ = schedAt(sc)(selectBlock)
                                                                                                                                in
                                                                                                                                    let _ = schedSet(sc)(scopeSlot)(outerScope)
                                                                                                                                    in
                                                                                                                                        let _ = schedBr(sc)(readyBlock)
                                                                                                                                        in
                                                                                                                                            let _ = schedAt(sc)(nextBlock)
                                                                                                                                            in
                                                                                                                                                let _ = schedSet(sc)(compositeSlot)(outer)
                                                                                                                                                in
                                                                                                                                                    let _ = schedBr(sc)(checkBlock)
                                                                                                                                                    in
                                                                                                                                                        let _ = schedAt(sc)(readyBlock)
                                                                                                                                                        in
                                                                                                                                                            let scope = schedLoad(sc)(scopeSlot)("fork_scope")
                                                                                                                                                            in
                                                                                                                                                                let node = emitArenaAlloc(sc.schedContext)(forkFn)(sc.schedBuilder)(sc.schedI64)(sc.schedArena)(24)("fork_node")
                                                                                                                                                                in
                                                                                                                                                                    let _ = schedStore(sc)(node)(0)(child)
                                                                                                                                                                    in
                                                                                                                                                                        let _ =
                                                                                                                                                                            0
                                                                                                                                                                            |> schedConst(sc)
                                                                                                                                                                            |> schedStore(sc)(node)(8)
                                                                                                                                                                        in
                                                                                                                                                                            let _ =
                                                                                                                                                                                0
                                                                                                                                                                                |> schedConst(sc)
                                                                                                                                                                                |> schedStore(sc)(node)(16)
                                                                                                                                                                            in
                                                                                                                                                                                let tail = schedWord(sc)(scope)(8)("fork_tail")
                                                                                                                                                                                in
                                                                                                                                                                                    let _ =
                                                                                                                                                                                        schedIf(sc)(schedEq(sc)(tail)(schedConst(sc)(0))("fork_empty"))(firstBlock)(appendBlock)
                                                                                                                                                                                    in
                                                                                                                                                                                        let _ = schedAt(sc)(firstBlock)
                                                                                                                                                                                        in
                                                                                                                                                                                            let _ = schedStore(sc)(scope)(0)(node)
                                                                                                                                                                                            in
                                                                                                                                                                                                let _ = schedBr(sc)(linkedBlock)
                                                                                                                                                                                                in
                                                                                                                                                                                                    let _ = schedAt(sc)(appendBlock)
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let _ = schedStore(sc)(tail)(16)(node)
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let _ = schedBr(sc)(linkedBlock)
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let _ = schedAt(sc)(linkedBlock)
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let _ = schedStore(sc)(scope)(8)(node)
                                                                                                                                                                                                                    in
                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                            0
                                                                                                                                                                                                                            |> schedConst(sc)
                                                                                                                                                                                                                            |> schedStore(sc)(child)(offsetWaiter)
                                                                                                                                                                                                                        in
                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                "fork_owner"
                                                                                                                                                                                                                                |> schedWord(sc)(composite)(offsetArenaOwner)
                                                                                                                                                                                                                                |> schedStore(sc)(child)(offsetArenaOwner)
                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                let _ = schedEnqueue(sc)(child)
                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                    let handleResult = schedOk(sc)(forkFn)(node)("fork_handle")
                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                            "fork_task"
                                                                                                                                                                                                                                            |> emitCreateCompletedTask(sc.schedContext)(forkFn)(sc.schedBuilder)(sc.schedI64)(sc.schedI8)(sc.schedPtr)(sc.schedArena)(handleResult)
                                                                                                                                                                                                                                            |> buildRet(sc.schedBuilder)
                                                                                                                                                                                                                                        in (forkFn, forkType))

// "Ashes.Net.Tcp.connect() failed": stage 0's `TcpConnectFailedMessage`.
let tcpConnectFailedCodes = [65, 115, 104, 101, 115, 46, 78, 101, 116, 46, 84, 99, 112, 46, 99, 111, 110, 110, 101, 99, 116, 40, 41, 32, 102, 97, 105, 108, 101, 100]

let recursive storeStringBytes (sc: SchedulerContext) baseRef (index: Int) (codes: List(Int)) =
    match codes with
        | [] -> Unit
        | code :: rest ->
            let _ =
                "string_byte"
                |> memOffsetPtr(sc.schedBuilder)(sc.schedI64)(sc.schedI8)(sc.schedPtr)(baseRef)(8 + index)
                |> buildStore(sc.schedBuilder)(constInt(sc.schedI8)(Ashes.Number.UInt.fromInt64(code))(false))
            in storeStringBytes(sc)(baseRef)(index + 1)(rest)

// A string with fixed ASCII contents in the arena, `[len][bytes]`.
let schedStringLiteral (sc: SchedulerContext) function_ (codes: List(Int)) name =
    (let length = Ashes.Collection.List.length(codes)
    in
        let text = emitArenaAlloc(sc.schedContext)(function_)(sc.schedBuilder)(sc.schedI64)(sc.schedArena)(8 + length)(name)
        in
            let _ =
                length
                |> schedConst(sc)
                |> schedStore(sc)(text)(0)
            in
                let _ = storeStringBytes(sc)(text)(0)(codes)
                in text)

// `Error(message)` as a fresh arena cell, `[tag 1][message]`.
let schedError (sc: SchedulerContext) function_ messageRef name =
    (let cell = emitArenaAlloc(sc.schedContext)(function_)(sc.schedBuilder)(sc.schedI64)(sc.schedArena)(16)(name)
    in
        let _ =
            1
            |> schedConst(sc)
            |> schedStore(sc)(cell)(0)
        in
            let _ = schedStore(sc)(cell)(8)(messageRef)
            in cell)

// Stage 0's `EmitResolveHostIpv4OrLocalhost` without the result cell: `localhost` or a dotted
// IPv4 address, as the 32-bit address with octet `i` in bits `8 i` (network order in memory).
// Answers whether it resolved, and the address; the parse runs in a loop over the host's bytes.
let emitResolveHost (sc: SchedulerContext) function_ hostRef =
    (let block =
        given (name) -> appendBasicBlock(sc.schedContext)(function_)(name)
    in
        let okSlot = schedSlot(sc)("resolve_ok")
        in
            let addressSlot = schedSlot(sc)("resolve_address")
            in
                let indexSlot = schedSlot(sc)("resolve_index")
                in
                    let partSlot = schedSlot(sc)("resolve_part")
                    in
                        let currentSlot = schedSlot(sc)("resolve_current")
                        in
                            let seenSlot = schedSlot(sc)("resolve_seen_digit")
                            in
                                match emitStringParts(sc.schedBuilder)(sc.schedI64)(sc.schedPtr)(hostRef)("resolve_host") with
                                    | (hostLength, hostBytes) ->
                                        let localhostBlock = block("resolve_localhost")
                                        in
                                            let notLocalhostBlock = block("resolve_not_localhost")
                                            in
                                                let loopBlock = block("resolve_loop")
                                                in
                                                    let inspectBlock = block("resolve_inspect")
                                                    in
                                                        let digitBlock = block("resolve_digit")
                                                        in
                                                            let dotCheckBlock = block("resolve_dot_check")
                                                            in
                                                                let dotBlock = block("resolve_dot")
                                                                in
                                                                    let finalizeBlock = block("resolve_finalize")
                                                                    in
                                                                        let doneBlock = block("resolve_done")
                                                                        in
                                                                            let _ =
                                                                                0
                                                                                |> schedConst(sc)
                                                                                |> schedSet(sc)(okSlot)
                                                                            in
                                                                                let _ =
                                                                                    0
                                                                                    |> schedConst(sc)
                                                                                    |> schedSet(sc)(addressSlot)
                                                                                in
                                                                                    let _ =
                                                                                        0
                                                                                        |> schedConst(sc)
                                                                                        |> schedSet(sc)(indexSlot)
                                                                                    in
                                                                                        let _ =
                                                                                            0
                                                                                            |> schedConst(sc)
                                                                                            |> schedSet(sc)(partSlot)
                                                                                        in
                                                                                            let _ =
                                                                                                0
                                                                                                |> schedConst(sc)
                                                                                                |> schedSet(sc)(currentSlot)
                                                                                            in
                                                                                                let _ =
                                                                                                    0
                                                                                                    |> schedConst(sc)
                                                                                                    |> schedSet(sc)(seenSlot)
                                                                                                in
                                                                                                    let lengthIsNine =
                                                                                                        schedEq(sc)(hostLength)(schedConst(sc)(9))("resolve_length_nine")
                                                                                                    in
                                                                                                        let _ = schedIf(sc)(lengthIsNine)(notLocalhostBlock)(loopBlock)
                                                                                                        in
                                                                                                            let _ = schedAt(sc)(notLocalhostBlock)
                                                                                                            in
                                                                                                                let firstWord =
                                                                                                                    buildLoad(sc.schedBuilder)(sc.schedI64)(buildIntToPtr(sc.schedBuilder)(hostBytes)(sc.schedPtr)("resolve_first_word_ptr"))("resolve_first_word")
                                                                                                                in
                                                                                                                    let lastByte =
                                                                                                                        buildLoad(sc.schedBuilder)(sc.schedI8)(memOffsetPtr(sc.schedBuilder)(sc.schedI64)(sc.schedI8)(sc.schedPtr)(hostBytes)(8)("resolve_last_byte_ptr"))("resolve_last_byte")
                                                                                                                    in
                                                                                                                        let startsLocalhos =
                                                                                                                            schedEq(sc)(firstWord)(constInt(sc.schedI64)(8317981851476258668u64)(false))("resolve_localhos")
                                                                                                                        in
                                                                                                                            let endsT =
                                                                                                                                buildICmp(sc.schedBuilder)(intPredicateEq)(lastByte)(constInt(sc.schedI8)(116u64)(false))("resolve_ends_t")
                                                                                                                            in
                                                                                                                                let _ =
                                                                                                                                    schedIf(sc)(buildAnd(sc.schedBuilder)(startsLocalhos)(endsT)("resolve_is_localhost"))(localhostBlock)(loopBlock)
                                                                                                                                in
                                                                                                                                    let _ = schedAt(sc)(localhostBlock)
                                                                                                                                    in
                                                                                                                                        let _ =
                                                                                                                                            16777343
                                                                                                                                            |> schedConst(sc)
                                                                                                                                            |> schedSet(sc)(addressSlot)
                                                                                                                                        in
                                                                                                                                            let _ =
                                                                                                                                                1
                                                                                                                                                |> schedConst(sc)
                                                                                                                                                |> schedSet(sc)(okSlot)
                                                                                                                                            in
                                                                                                                                                let _ = schedBr(sc)(doneBlock)
                                                                                                                                                in
                                                                                                                                                    let _ = schedAt(sc)(loopBlock)
                                                                                                                                                    in
                                                                                                                                                        let index = schedLoad(sc)(indexSlot)("resolve_index_value")
                                                                                                                                                        in
                                                                                                                                                            let _ =
                                                                                                                                                                schedIf(sc)(schedEq(sc)(index)(hostLength)("resolve_at_end"))(finalizeBlock)(inspectBlock)
                                                                                                                                                            in
                                                                                                                                                                let _ = schedAt(sc)(inspectBlock)
                                                                                                                                                                in
                                                                                                                                                                    let byte =
                                                                                                                                                                        buildZExt(sc.schedBuilder)(buildLoad(sc.schedBuilder)(sc.schedI8)(buildIntToPtr(sc.schedBuilder)(buildAdd(sc.schedBuilder)(hostBytes)(index)("resolve_byte_addr"))(sc.schedPtr)("resolve_byte_ptr"))("resolve_byte"))(sc.schedI64)("resolve_byte_wide")
                                                                                                                                                                    in
                                                                                                                                                                        let digit =
                                                                                                                                                                            buildSub(sc.schedBuilder)(byte)(schedConst(sc)(48))("resolve_digit_value")
                                                                                                                                                                        in
                                                                                                                                                                            let _ =
                                                                                                                                                                                schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateUlt)(digit)(schedConst(sc)(10))("resolve_is_digit"))(digitBlock)(dotCheckBlock)
                                                                                                                                                                            in
                                                                                                                                                                                let _ = schedAt(sc)(digitBlock)
                                                                                                                                                                                in
                                                                                                                                                                                    let next =
                                                                                                                                                                                        buildAdd(sc.schedBuilder)(buildMul(sc.schedBuilder)(schedLoad(sc)(currentSlot)("resolve_current_value"))(schedConst(sc)(10))("resolve_times_ten"))(digit)("resolve_next_value")
                                                                                                                                                                                    in
                                                                                                                                                                                        let storeDigitBlock = block("resolve_store_digit")
                                                                                                                                                                                        in
                                                                                                                                                                                            let _ =
                                                                                                                                                                                                schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateUgt)(next)(schedConst(sc)(255))("resolve_octet_too_large"))(doneBlock)(storeDigitBlock)
                                                                                                                                                                                            in
                                                                                                                                                                                                let _ = schedAt(sc)(storeDigitBlock)
                                                                                                                                                                                                in
                                                                                                                                                                                                    let _ = schedSet(sc)(currentSlot)(next)
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                            1
                                                                                                                                                                                                            |> schedConst(sc)
                                                                                                                                                                                                            |> schedSet(sc)(seenSlot)
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                "resolve_index_next"
                                                                                                                                                                                                                |> buildAdd(sc.schedBuilder)(index)(schedConst(sc)(1))
                                                                                                                                                                                                                |> schedSet(sc)(indexSlot)
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let _ = schedBr(sc)(loopBlock)
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let _ = schedAt(sc)(dotCheckBlock)
                                                                                                                                                                                                                    in
                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                            schedIf(sc)(schedEq(sc)(byte)(schedConst(sc)(46))("resolve_is_dot"))(dotBlock)(doneBlock)
                                                                                                                                                                                                                        in
                                                                                                                                                                                                                            let _ = schedAt(sc)(dotBlock)
                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                let part = schedLoad(sc)(partSlot)("resolve_part_value")
                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                    let storeDotBlock = block("resolve_store_dot")
                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                        let dotValid =
                                                                                                                                                                                                                                            buildAnd(sc.schedBuilder)(buildICmp(sc.schedBuilder)(intPredicateNe)(schedLoad(sc)(seenSlot)("resolve_seen_value"))(schedConst(sc)(0))("resolve_dot_seen"))(buildICmp(sc.schedBuilder)(intPredicateUlt)(part)(schedConst(sc)(3))("resolve_part_below_three"))("resolve_dot_valid")
                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                            let _ = schedIf(sc)(dotValid)(storeDotBlock)(doneBlock)
                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                let _ = schedAt(sc)(storeDotBlock)
                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                    let shifted =
                                                                                                                                                                                                                                                        buildShl(sc.schedBuilder)(schedLoad(sc)(currentSlot)("resolve_octet"))(buildMul(sc.schedBuilder)(part)(schedConst(sc)(8))("resolve_octet_shift"))("resolve_shifted_octet")
                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                            "resolve_address_next"
                                                                                                                                                                                                                                                            |> buildOr(sc.schedBuilder)(schedLoad(sc)(addressSlot)("resolve_address_value"))(shifted)
                                                                                                                                                                                                                                                            |> schedSet(sc)(addressSlot)
                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                "resolve_part_next"
                                                                                                                                                                                                                                                                |> buildAdd(sc.schedBuilder)(part)(schedConst(sc)(1))
                                                                                                                                                                                                                                                                |> schedSet(sc)(partSlot)
                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                                                    0
                                                                                                                                                                                                                                                                    |> schedConst(sc)
                                                                                                                                                                                                                                                                    |> schedSet(sc)(currentSlot)
                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                                        0
                                                                                                                                                                                                                                                                        |> schedConst(sc)
                                                                                                                                                                                                                                                                        |> schedSet(sc)(seenSlot)
                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                                            "resolve_index_after_dot"
                                                                                                                                                                                                                                                                            |> buildAdd(sc.schedBuilder)(index)(schedConst(sc)(1))
                                                                                                                                                                                                                                                                            |> schedSet(sc)(indexSlot)
                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                            let _ = schedBr(sc)(loopBlock)
                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                let _ = schedAt(sc)(finalizeBlock)
                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                    let storeFinalBlock = block("resolve_store_final")
                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                        let finalValid =
                                                                                                                                                                                                                                                                                            buildAnd(sc.schedBuilder)(buildICmp(sc.schedBuilder)(intPredicateNe)(schedLoad(sc)(seenSlot)("resolve_final_seen"))(schedConst(sc)(0))("resolve_final_seen_digit"))(schedEq(sc)(schedLoad(sc)(partSlot)("resolve_final_part"))(schedConst(sc)(3))("resolve_final_part_three"))("resolve_final_valid")
                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                            let _ = schedIf(sc)(finalValid)(storeFinalBlock)(doneBlock)
                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                let _ = schedAt(sc)(storeFinalBlock)
                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                                                                        "resolve_final_address"
                                                                                                                                                                                                                                                                                                        |> buildOr(sc.schedBuilder)(schedLoad(sc)(addressSlot)("resolve_address_before_final"))(buildShl(sc.schedBuilder)(schedLoad(sc)(currentSlot)("resolve_last_octet"))(schedConst(sc)(24))("resolve_last_shifted"))
                                                                                                                                                                                                                                                                                                        |> schedSet(sc)(addressSlot)
                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                                                                            1
                                                                                                                                                                                                                                                                                                            |> schedConst(sc)
                                                                                                                                                                                                                                                                                                            |> schedSet(sc)(okSlot)
                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                            let _ = schedBr(sc)(doneBlock)
                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                let _ = schedAt(sc)(doneBlock)
                                                                                                                                                                                                                                                                                                                in (schedLoad(sc)(okSlot)("resolve_ok_value"), schedLoad(sc)(addressSlot)("resolve_address_final")))

// Stage 0's `ashes_step_tcp_connect_task` for Linux: the first step resolves the host, checks the
// port, opens a non-blocking socket and keeps it and the address in `WaitData0`/`WaitData1`; every
// step then connects. A finished connect completes with `Ok(socket)`; one in progress parks on the
// socket becoming writable; anything else completes with stage 0's failure message. Answers 1 when
// the task completed.
let defineStepTcpConnect module_ (sc: SchedulerContext) =
    (let stepType = functionType(sc.schedI64)([sc.schedI64])(1u32)(false)
    in
        let stepFn = addInternalFunction(module_)("__ashes_step_tcp_connect")(stepType)
        in
            let block =
                given (name) -> appendBasicBlock(sc.schedContext)(stepFn)(name)
            in
                let entryBlock = block("entry")
                in
                    let setupBlock = block("tcp_connect_setup")
                    in
                        let openBlock = block("tcp_connect_open")
                        in
                            let connectBlock = block("tcp_connect_connect")
                            in
                                let pendingCheckBlock = block("tcp_connect_pending_check")
                                in
                                    let successBlock = block("tcp_connect_success")
                                    in
                                        let pendingBlock = block("tcp_connect_pending")
                                        in
                                            let failBlock = block("tcp_connect_fail")
                                            in
                                                let task = getParam(stepFn)(0u32)
                                                in
                                                    let _ = schedAt(sc)(entryBlock)
                                                    in
                                                        let sockaddr =
                                                            buildAlloca(sc.schedBuilder)(arrayType(sc.schedI64)(2u64))("tcp_connect_sockaddr")
                                                        in
                                                            let port = schedWord(sc)(task)(offsetIoArgument1)("tcp_connect_port")
                                                            in
                                                                let _ =
                                                                    schedIf(sc)(schedEq(sc)(schedWord(sc)(task)(offsetWaitData0)("tcp_connect_cached_socket"))(schedConst(sc)(0))("tcp_connect_first_step"))(setupBlock)(connectBlock)
                                                                in
                                                                    let _ = schedAt(sc)(setupBlock)
                                                                    in
                                                                        match "tcp_connect_host"
                                                                        |> schedWord(sc)(task)(offsetIoArgument0)
                                                                        |> emitResolveHost(sc)(stepFn) with
                                                                            | (resolved, address) ->
                                                                                let portValid =
                                                                                    buildAnd(sc.schedBuilder)(buildICmp(sc.schedBuilder)(intPredicateSgt)(port)(schedConst(sc)(0))("tcp_connect_port_positive"))(buildICmp(sc.schedBuilder)(intPredicateSle)(port)(schedConst(sc)(65535))("tcp_connect_port_in_range"))("tcp_connect_port_valid")
                                                                                in
                                                                                    let _ =
                                                                                        schedIf(sc)(buildAnd(sc.schedBuilder)(buildICmp(sc.schedBuilder)(intPredicateNe)(resolved)(schedConst(sc)(0))("tcp_connect_resolved"))(portValid)("tcp_connect_can_open"))(openBlock)(failBlock)
                                                                                    in
                                                                                        let _ = schedAt(sc)(openBlock)
                                                                                        in
                                                                                            let socket =
                                                                                                emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(41))(schedConst(sc)(2))(schedConst(sc)(1))(schedConst(sc)(0))("tcp_connect_socket")
                                                                                            in
                                                                                                let _ = schedStore(sc)(task)(offsetWaitData0)(socket)
                                                                                                in
                                                                                                    let _ = schedStore(sc)(task)(offsetWaitData1)(address)
                                                                                                    in
                                                                                                        let flags =
                                                                                                            emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(72))(socket)(schedConst(sc)(3))(schedConst(sc)(0))("tcp_connect_get_flags")
                                                                                                        in
                                                                                                            let _ =
                                                                                                                emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(72))(socket)(schedConst(sc)(4))(buildOr(sc.schedBuilder)(flags)(schedConst(sc)(2048))("tcp_connect_nonblocking_flags"))("tcp_connect_set_flags")
                                                                                                            in
                                                                                                                let _ = schedBr(sc)(connectBlock)
                                                                                                                in
                                                                                                                    let _ = schedAt(sc)(connectBlock)
                                                                                                                    in
                                                                                                                        let connectSocket = schedWord(sc)(task)(offsetWaitData0)("tcp_connect_socket_value")
                                                                                                                        in
                                                                                                                            let portNetwork =
                                                                                                                                buildOr(sc.schedBuilder)(buildMul(sc.schedBuilder)(buildAnd(sc.schedBuilder)(port)(schedConst(sc)(255))("tcp_connect_port_low"))(schedConst(sc)(256))("tcp_connect_port_low_shifted"))(buildAnd(sc.schedBuilder)(buildSDiv(sc.schedBuilder)(port)(schedConst(sc)(256))("tcp_connect_port_high"))(schedConst(sc)(255))("tcp_connect_port_high_masked"))("tcp_connect_port_network")
                                                                                                                            in
                                                                                                                                let firstWord =
                                                                                                                                    buildOr(sc.schedBuilder)(buildOr(sc.schedBuilder)(schedConst(sc)(2))(buildShl(sc.schedBuilder)(portNetwork)(schedConst(sc)(16))("tcp_connect_port_field"))("tcp_connect_family_port"))(buildShl(sc.schedBuilder)(schedWord(sc)(task)(offsetWaitData1)("tcp_connect_address"))(schedConst(sc)(32))("tcp_connect_address_field"))("tcp_connect_sockaddr_word")
                                                                                                                                in
                                                                                                                                    let sockaddrAddress = buildPtrToInt(sc.schedBuilder)(sockaddr)(sc.schedI64)("tcp_connect_sockaddr_address")
                                                                                                                                    in
                                                                                                                                        let _ = schedStore(sc)(sockaddrAddress)(0)(firstWord)
                                                                                                                                        in
                                                                                                                                            let _ =
                                                                                                                                                0
                                                                                                                                                |> schedConst(sc)
                                                                                                                                                |> schedStore(sc)(sockaddrAddress)(8)
                                                                                                                                            in
                                                                                                                                                let result =
                                                                                                                                                    emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(42))(connectSocket)(sockaddrAddress)(schedConst(sc)(16))("tcp_connect_call")
                                                                                                                                                in
                                                                                                                                                    let succeeded =
                                                                                                                                                        buildOr(sc.schedBuilder)(buildICmp(sc.schedBuilder)(intPredicateSge)(result)(schedConst(sc)(0))("tcp_connect_ok"))(schedEq(sc)(result)(schedConst(sc)(-106))("tcp_connect_is_connected"))("tcp_connect_succeeded")
                                                                                                                                                    in
                                                                                                                                                        let _ = schedIf(sc)(succeeded)(successBlock)(pendingCheckBlock)
                                                                                                                                                        in
                                                                                                                                                            let _ = schedAt(sc)(pendingCheckBlock)
                                                                                                                                                            in
                                                                                                                                                                let pending =
                                                                                                                                                                    buildOr(sc.schedBuilder)(schedEq(sc)(result)(schedConst(sc)(-115))("tcp_connect_in_progress"))(schedEq(sc)(result)(schedConst(sc)(-114))("tcp_connect_already"))("tcp_connect_is_pending")
                                                                                                                                                                in
                                                                                                                                                                    let _ = schedIf(sc)(pending)(pendingBlock)(failBlock)
                                                                                                                                                                    in
                                                                                                                                                                        let _ = schedAt(sc)(successBlock)
                                                                                                                                                                        in
                                                                                                                                                                            let _ =
                                                                                                                                                                                "tcp_connect_result"
                                                                                                                                                                                |> schedOk(sc)(stepFn)(connectSocket)
                                                                                                                                                                                |> schedStore(sc)(task)(offsetResultSlot)
                                                                                                                                                                            in
                                                                                                                                                                                let _ =
                                                                                                                                                                                    0
                                                                                                                                                                                    |> schedConst(sc)
                                                                                                                                                                                    |> schedStore(sc)(task)(offsetWaitKind)
                                                                                                                                                                                in
                                                                                                                                                                                    let _ =
                                                                                                                                                                                        CompletedTaskState
                                                                                                                                                                                        |> stateConst(sc.schedI64)
                                                                                                                                                                                        |> schedStore(sc)(task)(offsetStateIndex)
                                                                                                                                                                                    in
                                                                                                                                                                                        let _ =
                                                                                                                                                                                            1
                                                                                                                                                                                            |> schedConst(sc)
                                                                                                                                                                                            |> buildRet(sc.schedBuilder)
                                                                                                                                                                                        in
                                                                                                                                                                                            let _ = schedAt(sc)(pendingBlock)
                                                                                                                                                                                            in
                                                                                                                                                                                                let _ =
                                                                                                                                                                                                    SocketWriteTaskWait
                                                                                                                                                                                                    |> taskWaitCode
                                                                                                                                                                                                    |> wordConst(sc.schedI64)
                                                                                                                                                                                                    |> schedStore(sc)(task)(offsetWaitKind)
                                                                                                                                                                                                in
                                                                                                                                                                                                    let _ = schedStore(sc)(task)(offsetWaitHandle)(connectSocket)
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                            0
                                                                                                                                                                                                            |> schedConst(sc)
                                                                                                                                                                                                            |> buildRet(sc.schedBuilder)
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let _ = schedAt(sc)(failBlock)
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let message = schedStringLiteral(sc)(stepFn)(tcpConnectFailedCodes)("tcp_connect_message")
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                        "tcp_connect_failure"
                                                                                                                                                                                                                        |> schedError(sc)(stepFn)(message)
                                                                                                                                                                                                                        |> schedStore(sc)(task)(offsetResultSlot)
                                                                                                                                                                                                                    in
                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                            0
                                                                                                                                                                                                                            |> schedConst(sc)
                                                                                                                                                                                                                            |> schedStore(sc)(task)(offsetWaitKind)
                                                                                                                                                                                                                        in
                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                CompletedTaskState
                                                                                                                                                                                                                                |> stateConst(sc.schedI64)
                                                                                                                                                                                                                                |> schedStore(sc)(task)(offsetStateIndex)
                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                    1
                                                                                                                                                                                                                                    |> schedConst(sc)
                                                                                                                                                                                                                                    |> buildRet(sc.schedBuilder)
                                                                                                                                                                                                                                in (stepFn, stepType))

// "Ashes: HTTP is not supported by the self-hosted compiler yet\n".
let unportedHttpMessageCodes = [65, 115, 104, 101, 115, 58, 32, 72, 84, 84, 80, 32, 105, 115, 32, 110, 111, 116, 32, 115, 117, 112, 112, 111, 114, 116, 101, 100, 32, 98, 121, 32, 116, 104, 101, 32, 115, 101, 108, 102, 45, 104, 111, 115, 116, 101, 100, 32, 99, 111, 109, 112, 105, 108, 101, 114, 32, 121, 101, 116, 10]

// The scheme, host and port of an `http://` or `https://` URL, the connect stage of stage 0's
// `EmitStepHttpTask`: the host runs to the first `:` or `/`, a port follows a `:`, and the default
// port is 80 or 443. Answers whether the URL has one of the two schemes, the host as a fresh arena
// string, and the port.
let emitParseHttpTarget (sc: SchedulerContext) function_ urlRef =
    (let block =
        given (name) -> appendBasicBlock(sc.schedContext)(function_)(name)
    in
        let okSlot = schedSlot(sc)("http_url_ok")
        in
            let startSlot = schedSlot(sc)("http_host_start")
            in
                let endSlot = schedSlot(sc)("http_host_end")
                in
                    let portSlot = schedSlot(sc)("http_port")
                    in
                        let indexSlot = schedSlot(sc)("http_index")
                        in
                            match emitStringParts(sc.schedBuilder)(sc.schedI64)(sc.schedPtr)(urlRef)("http_url") with
                                | (urlLength, urlBytes) ->
                                    let schemeBlock = block("http_scheme")
                                    in
                                        let httpsBlock = block("http_is_https")
                                        in
                                            let httpCheckBlock = block("http_http_check")
                                            in
                                                let httpBlock = block("http_is_http")
                                                in
                                                    let hostLoopBlock = block("http_host_loop")
                                                    in
                                                        let hostByteBlock = block("http_host_byte")
                                                        in
                                                            let hostNextBlock = block("http_host_next")
                                                            in
                                                                let hostEndBlock = block("http_host_end")
                                                                in
                                                                    let portStartBlock = block("http_port_start")
                                                                    in
                                                                        let portLoopBlock = block("http_port_loop")
                                                                        in
                                                                            let portByteBlock = block("http_port_byte")
                                                                            in
                                                                                let portDigitBlock = block("http_port_digit")
                                                                                in
                                                                                    let doneBlock = block("http_url_done")
                                                                                    in
                                                                                        let byteAt =
                                                                                            given (index) ->
                                                                                                buildZExt(sc.schedBuilder)(buildLoad(sc.schedBuilder)(sc.schedI8)(buildIntToPtr(sc.schedBuilder)(buildAdd(sc.schedBuilder)(urlBytes)(index)("http_byte_addr"))(sc.schedPtr)("http_byte_ptr"))("http_byte"))(sc.schedI64)("http_byte_wide")
                                                                                        in
                                                                                            let _ =
                                                                                                0
                                                                                                |> schedConst(sc)
                                                                                                |> schedSet(sc)(okSlot)
                                                                                            in
                                                                                                let _ =
                                                                                                    schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateSge)(urlLength)(schedConst(sc)(8))("http_long_enough"))(schemeBlock)(doneBlock)
                                                                                                in
                                                                                                    let _ = schedAt(sc)(schemeBlock)
                                                                                                    in
                                                                                                        let firstWord =
                                                                                                            buildLoad(sc.schedBuilder)(sc.schedI64)(buildIntToPtr(sc.schedBuilder)(urlBytes)(sc.schedPtr)("http_scheme_ptr"))("http_scheme_word")
                                                                                                        in
                                                                                                            let _ =
                                                                                                                schedIf(sc)(schedEq(sc)(firstWord)(constInt(sc.schedI64)(3400000511170344040u64)(false))("http_https_prefix"))(httpsBlock)(httpCheckBlock)
                                                                                                            in
                                                                                                                let _ = schedAt(sc)(httpsBlock)
                                                                                                                in
                                                                                                                    let _ =
                                                                                                                        8
                                                                                                                        |> schedConst(sc)
                                                                                                                        |> schedSet(sc)(startSlot)
                                                                                                                    in
                                                                                                                        let _ =
                                                                                                                            443
                                                                                                                            |> schedConst(sc)
                                                                                                                            |> schedSet(sc)(portSlot)
                                                                                                                        in
                                                                                                                            let _ = schedBr(sc)(hostLoopBlock)
                                                                                                                            in
                                                                                                                                let _ = schedAt(sc)(httpCheckBlock)
                                                                                                                                in
                                                                                                                                    let _ =
                                                                                                                                        schedIf(sc)(schedEq(sc)(buildAnd(sc.schedBuilder)(firstWord)(constInt(sc.schedI64)(72057594037927935u64)(false))("http_first_seven"))(constInt(sc.schedI64)(13281251946689640u64)(false))("http_http_prefix"))(httpBlock)(doneBlock)
                                                                                                                                    in
                                                                                                                                        let _ = schedAt(sc)(httpBlock)
                                                                                                                                        in
                                                                                                                                            let _ =
                                                                                                                                                7
                                                                                                                                                |> schedConst(sc)
                                                                                                                                                |> schedSet(sc)(startSlot)
                                                                                                                                            in
                                                                                                                                                let _ =
                                                                                                                                                    80
                                                                                                                                                    |> schedConst(sc)
                                                                                                                                                    |> schedSet(sc)(portSlot)
                                                                                                                                                in
                                                                                                                                                    let _ = schedBr(sc)(hostLoopBlock)
                                                                                                                                                    in
                                                                                                                                                        let _ = schedAt(sc)(hostLoopBlock)
                                                                                                                                                        in
                                                                                                                                                            let _ =
                                                                                                                                                                "http_host_first"
                                                                                                                                                                |> schedLoad(sc)(startSlot)
                                                                                                                                                                |> schedSet(sc)(indexSlot)
                                                                                                                                                            in
                                                                                                                                                                let hostCheckBlock = block("http_host_check")
                                                                                                                                                                in
                                                                                                                                                                    let _ = schedBr(sc)(hostCheckBlock)
                                                                                                                                                                    in
                                                                                                                                                                        let _ = schedAt(sc)(hostCheckBlock)
                                                                                                                                                                        in
                                                                                                                                                                            let hostIndex = schedLoad(sc)(indexSlot)("http_host_index")
                                                                                                                                                                            in
                                                                                                                                                                                let _ =
                                                                                                                                                                                    schedIf(sc)(schedEq(sc)(hostIndex)(urlLength)("http_host_at_end"))(hostEndBlock)(hostByteBlock)
                                                                                                                                                                                in
                                                                                                                                                                                    let _ = schedAt(sc)(hostByteBlock)
                                                                                                                                                                                    in
                                                                                                                                                                                        let hostByte = byteAt(hostIndex)
                                                                                                                                                                                        in
                                                                                                                                                                                            let _ =
                                                                                                                                                                                                schedIf(sc)(buildOr(sc.schedBuilder)(schedEq(sc)(hostByte)(schedConst(sc)(58))("http_host_colon"))(schedEq(sc)(hostByte)(schedConst(sc)(47))("http_host_slash"))("http_host_stop"))(hostEndBlock)(hostNextBlock)
                                                                                                                                                                                            in
                                                                                                                                                                                                let _ = schedAt(sc)(hostNextBlock)
                                                                                                                                                                                                in
                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                        "http_host_index_next"
                                                                                                                                                                                                        |> buildAdd(sc.schedBuilder)(hostIndex)(schedConst(sc)(1))
                                                                                                                                                                                                        |> schedSet(sc)(indexSlot)
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let _ = schedBr(sc)(hostCheckBlock)
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let _ = schedAt(sc)(hostEndBlock)
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let hostEnd = schedLoad(sc)(indexSlot)("http_host_end_index")
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let _ = schedSet(sc)(endSlot)(hostEnd)
                                                                                                                                                                                                                    in
                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                            1
                                                                                                                                                                                                                            |> schedConst(sc)
                                                                                                                                                                                                                            |> schedSet(sc)(okSlot)
                                                                                                                                                                                                                        in
                                                                                                                                                                                                                            let hasPortBlock = block("http_has_port")
                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                    schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateSlt)(hostEnd)(urlLength)("http_host_not_last"))(hasPortBlock)(doneBlock)
                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                    let _ = schedAt(sc)(hasPortBlock)
                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                            schedIf(sc)(schedEq(sc)(byteAt(hostEnd))(schedConst(sc)(58))("http_port_colon"))(portStartBlock)(doneBlock)
                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                            let _ = schedAt(sc)(portStartBlock)
                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                                    0
                                                                                                                                                                                                                                                    |> schedConst(sc)
                                                                                                                                                                                                                                                    |> schedSet(sc)(portSlot)
                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                        "http_port_first"
                                                                                                                                                                                                                                                        |> buildAdd(sc.schedBuilder)(hostEnd)(schedConst(sc)(1))
                                                                                                                                                                                                                                                        |> schedSet(sc)(indexSlot)
                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                        let _ = schedBr(sc)(portLoopBlock)
                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                            let _ = schedAt(sc)(portLoopBlock)
                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                let portIndex = schedLoad(sc)(indexSlot)("http_port_index")
                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                                        schedIf(sc)(schedEq(sc)(portIndex)(urlLength)("http_port_at_end"))(doneBlock)(portByteBlock)
                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                        let _ = schedAt(sc)(portByteBlock)
                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                            let portDigit =
                                                                                                                                                                                                                                                                                buildSub(sc.schedBuilder)(byteAt(portIndex))(schedConst(sc)(48))("http_port_digit_value")
                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                                                                    schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateUlt)(portDigit)(schedConst(sc)(10))("http_port_is_digit"))(portDigitBlock)(doneBlock)
                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                    let _ = schedAt(sc)(portDigitBlock)
                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                                                            "http_port_next"
                                                                                                                                                                                                                                                                                            |> buildAdd(sc.schedBuilder)(buildMul(sc.schedBuilder)(schedLoad(sc)(portSlot)("http_port_value"))(schedConst(sc)(10))("http_port_times_ten"))(portDigit)
                                                                                                                                                                                                                                                                                            |> schedSet(sc)(portSlot)
                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                "http_port_index_next"
                                                                                                                                                                                                                                                                                                |> buildAdd(sc.schedBuilder)(portIndex)(schedConst(sc)(1))
                                                                                                                                                                                                                                                                                                |> schedSet(sc)(indexSlot)
                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                let _ = schedBr(sc)(portLoopBlock)
                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                    let _ = schedAt(sc)(doneBlock)
                                                                                                                                                                                                                                                                                                    in (schedLoad(sc)(okSlot)("http_url_ok_value"), schedLoad(sc)(startSlot)("http_host_start_value"), schedLoad(sc)(endSlot)("http_host_end_value"), schedLoad(sc)(portSlot)("http_port_final"), urlBytes))

// The bytes `[start, end)` at `bytesAddr` as a fresh arena string, copied a byte at a time.
let emitArenaSubstring (sc: SchedulerContext) function_ bytesAddr start end_ name =
    (let length = buildSub(sc.schedBuilder)(end_)(start)(name + "_length")
    in
        let text =
            emitArenaAllocDynamic(sc.schedContext)(function_)(sc.schedBuilder)(sc.schedI64)(sc.schedArena)(buildAnd(sc.schedBuilder)(buildAdd(sc.schedBuilder)(length)(schedConst(sc)(15))(name + "_size_plus"))(schedConst(sc)(-8))(name + "_size"))(name)
        in
            let indexSlot = schedSlot(sc)(name + "_index")
            in
                let loopBlock = appendBasicBlock(sc.schedContext)(function_)(name + "_copy_loop")
                in
                    let bodyBlock = appendBasicBlock(sc.schedContext)(function_)(name + "_copy_byte")
                    in
                        let doneBlock = appendBasicBlock(sc.schedContext)(function_)(name + "_copied")
                        in
                            let _ = schedStore(sc)(text)(0)(length)
                            in
                                let _ =
                                    0
                                    |> schedConst(sc)
                                    |> schedSet(sc)(indexSlot)
                                in
                                    let _ = schedBr(sc)(loopBlock)
                                    in
                                        let _ = schedAt(sc)(loopBlock)
                                        in
                                            let index = schedLoad(sc)(indexSlot)(name + "_index_value")
                                            in
                                                let _ =
                                                    schedIf(sc)(schedEq(sc)(index)(length)(name + "_at_end"))(doneBlock)(bodyBlock)
                                                in
                                                    let _ = schedAt(sc)(bodyBlock)
                                                    in
                                                        let byte =
                                                            buildLoad(sc.schedBuilder)(sc.schedI8)(buildIntToPtr(sc.schedBuilder)(buildAdd(sc.schedBuilder)(bytesAddr)(buildAdd(sc.schedBuilder)(start)(index)(name + "_source_index"))(name + "_source_addr"))(sc.schedPtr)(name + "_source_ptr"))(name + "_byte")
                                                        in
                                                            let _ =
                                                                name + "_dest_ptr"
                                                                |> buildIntToPtr(sc.schedBuilder)(buildAdd(sc.schedBuilder)(text)(buildAdd(sc.schedBuilder)(index)(schedConst(sc)(8))(name + "_dest_index"))(name + "_dest_addr"))(sc.schedPtr)
                                                                |> buildStore(sc.schedBuilder)(byte)
                                                            in
                                                                let _ =
                                                                    name + "_index_next"
                                                                    |> buildAdd(sc.schedBuilder)(index)(schedConst(sc)(1))
                                                                    |> schedSet(sc)(indexSlot)
                                                                in
                                                                    let _ = schedBr(sc)(loopBlock)
                                                                    in
                                                                        let _ = schedAt(sc)(doneBlock)
                                                                        in text)

// The connect stage of stage 0's HTTP leaf: the first step parses the URL and creates a TCP connect
// task for its host and port, every step steps that task, and a failed connect completes the HTTP
// task with the connect's error. A request over a connected socket is not ported, so a connect that
// succeeds stops the program. Answers 1 when the task completed.
let defineStepHttp module_ (sc: SchedulerContext) connectFn connectType =
    (let stepType = functionType(sc.schedI64)([sc.schedI64])(1u32)(false)
    in
        let stepFn = addInternalFunction(module_)("__ashes_step_http")(stepType)
        in
            let block =
                given (name) -> appendBasicBlock(sc.schedContext)(stepFn)(name)
            in
                let entryBlock = block("entry")
                in
                    let createBlock = block("http_create_connect")
                    in
                        let parsedBlock = block("http_parsed")
                        in
                            let unportedBlock = block("http_unported")
                            in
                                let stepBlock = block("http_step_connect")
                                in
                                    let pendingBlock = block("http_connect_pending")
                                    in
                                        let connectedBlock = block("http_connected")
                                        in
                                            let failedBlock = block("http_connect_failed")
                                            in
                                                let task = getParam(stepFn)(0u32)
                                                in
                                                    let _ = schedAt(sc)(entryBlock)
                                                    in
                                                        let _ =
                                                            schedIf(sc)(schedEq(sc)(schedWord(sc)(task)(offsetAwaitedTask)("http_child"))(schedConst(sc)(0))("http_first_step"))(createBlock)(stepBlock)
                                                        in
                                                            let _ = schedAt(sc)(unportedBlock)
                                                            in
                                                                let _ = emitProcessPanicMessage(sc.schedBuilder)(sc.schedI64)(sc.schedI8)(unportedHttpMessageCodes)("http_unported")
                                                                in
                                                                    let _ = schedAt(sc)(createBlock)
                                                                    in
                                                                        match "http_url_value"
                                                                        |> schedWord(sc)(task)(offsetIoArgument0)
                                                                        |> emitParseHttpTarget(sc)(stepFn) with
                                                                            | (parsed, hostStart, hostEnd, port, urlBytes) ->
                                                                                let _ =
                                                                                    schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateNe)(parsed)(schedConst(sc)(0))("http_url_parsed"))(parsedBlock)(unportedBlock)
                                                                                in
                                                                                    let _ = schedAt(sc)(parsedBlock)
                                                                                    in
                                                                                        let host = emitArenaSubstring(sc)(stepFn)(urlBytes)(hostStart)(hostEnd)("http_host")
                                                                                        in
                                                                                            let child =
                                                                                                emitLeafTask(sc.schedContext)(stepFn)(sc.schedBuilder)(sc.schedI64)(sc.schedI8)(sc.schedPtr)(sc.schedArena)(TcpConnectTaskState)(host)(port)(schedConst(sc)(0))("http_connect_task")
                                                                                            in
                                                                                                let _ = schedStore(sc)(task)(offsetAwaitedTask)(child)
                                                                                                in
                                                                                                    let _ = schedBr(sc)(stepBlock)
                                                                                                    in
                                                                                                        let _ = schedAt(sc)(stepBlock)
                                                                                                        in
                                                                                                            let connectTask = schedWord(sc)(task)(offsetAwaitedTask)("http_connect_child")
                                                                                                            in
                                                                                                                let status = buildCall(sc.schedBuilder)(connectType)(connectFn)([connectTask])(1u32)("http_connect_status")
                                                                                                                in
                                                                                                                    let _ =
                                                                                                                        schedIf(sc)(schedEq(sc)(status)(schedConst(sc)(0))("http_connect_waiting"))(pendingBlock)(connectedBlock)
                                                                                                                    in
                                                                                                                        let _ = schedAt(sc)(pendingBlock)
                                                                                                                        in
                                                                                                                            let _ =
                                                                                                                                "http_child_wait_kind"
                                                                                                                                |> schedWord(sc)(connectTask)(offsetWaitKind)
                                                                                                                                |> schedStore(sc)(task)(offsetWaitKind)
                                                                                                                            in
                                                                                                                                let _ =
                                                                                                                                    "http_child_wait_handle"
                                                                                                                                    |> schedWord(sc)(connectTask)(offsetWaitHandle)
                                                                                                                                    |> schedStore(sc)(task)(offsetWaitHandle)
                                                                                                                                in
                                                                                                                                    let _ =
                                                                                                                                        0
                                                                                                                                        |> schedConst(sc)
                                                                                                                                        |> buildRet(sc.schedBuilder)
                                                                                                                                    in
                                                                                                                                        let _ = schedAt(sc)(connectedBlock)
                                                                                                                                        in
                                                                                                                                            let connectResult = schedWord(sc)(connectTask)(offsetResultSlot)("http_connect_result")
                                                                                                                                            in
                                                                                                                                                let _ =
                                                                                                                                                    schedIf(sc)(schedEq(sc)(schedWord(sc)(connectResult)(0)("http_connect_tag"))(schedConst(sc)(0))("http_connect_ok"))(unportedBlock)(failedBlock)
                                                                                                                                                in
                                                                                                                                                    let _ = schedAt(sc)(failedBlock)
                                                                                                                                                    in
                                                                                                                                                        let _ = schedStore(sc)(task)(offsetResultSlot)(connectResult)
                                                                                                                                                        in
                                                                                                                                                            let _ =
                                                                                                                                                                0
                                                                                                                                                                |> schedConst(sc)
                                                                                                                                                                |> schedStore(sc)(task)(offsetWaitKind)
                                                                                                                                                            in
                                                                                                                                                                let _ =
                                                                                                                                                                    CompletedTaskState
                                                                                                                                                                    |> stateConst(sc.schedI64)
                                                                                                                                                                    |> schedStore(sc)(task)(offsetStateIndex)
                                                                                                                                                                in
                                                                                                                                                                    let _ =
                                                                                                                                                                        1
                                                                                                                                                                        |> schedConst(sc)
                                                                                                                                                                        |> buildRet(sc.schedBuilder)
                                                                                                                                                                    in (stepFn, stepType))

// Stage 0's networking failure messages.
let tcpSendFailedCodes = [65, 115, 104, 101, 115, 46, 78, 101, 116, 46, 84, 99, 112, 46, 115, 101, 110, 100, 40, 41, 32, 102, 97, 105, 108, 101, 100]

let tcpReceiveFailedCodes = [65, 115, 104, 101, 115, 46, 78, 101, 116, 46, 84, 99, 112, 46, 114, 101, 99, 101, 105, 118, 101, 40, 41, 32, 102, 97, 105, 108, 101, 100]

let tcpCloseFailedCodes = [65, 115, 104, 101, 115, 46, 78, 101, 116, 46, 84, 99, 112, 46, 99, 108, 111, 115, 101, 40, 41, 32, 102, 97, 105, 108, 101, 100]

let tcpInvalidMaxBytesCodes = [65, 115, 104, 101, 115, 46, 78, 101, 116, 46, 84, 99, 112, 46, 114, 101, 99, 101, 105, 118, 101, 40, 41, 32, 109, 97, 120, 66, 121, 116, 101, 115, 32, 109, 117, 115, 116, 32, 98, 101, 32, 112, 111, 115, 105, 116, 105, 118, 101]

let tcpInvalidUtf8Codes = [65, 115, 104, 101, 115, 46, 78, 101, 116, 46, 84, 99, 112, 46, 114, 101, 99, 101, 105, 118, 101, 40, 41, 32, 101, 110, 99, 111, 117, 110, 116, 101, 114, 101, 100, 32, 105, 110, 118, 97, 108, 105, 100, 32, 85, 84, 70, 45, 56]

let tcpListenFailedCodes = [65, 115, 104, 101, 115, 46, 78, 101, 116, 46, 84, 99, 112, 46, 83, 101, 114, 118, 101, 114, 46, 108, 105, 115, 116, 101, 110, 40, 41, 32, 102, 97, 105, 108, 101, 100]

let tcpAcceptFailedCodes = [65, 115, 104, 101, 115, 46, 78, 101, 116, 46, 84, 99, 112, 46, 83, 101, 114, 118, 101, 114, 46, 97, 99, 99, 101, 112, 116, 40, 41, 32, 102, 97, 105, 108, 101, 100]

// A leaf completes with `result` and leaves its wait; the step answers 1.
let schedCompleteLeaf (sc: SchedulerContext) task result =
    (let _ = schedStore(sc)(task)(offsetResultSlot)(result)
    in
        let _ =
            0
            |> schedConst(sc)
            |> schedStore(sc)(task)(offsetWaitKind)
        in
            let _ =
                CompletedTaskState
                |> stateConst(sc.schedI64)
                |> schedStore(sc)(task)(offsetStateIndex)
            in
                1
                |> schedConst(sc)
                |> buildRet(sc.schedBuilder))

let schedFailLeaf (sc: SchedulerContext) function_ task (codes: List(Int)) name =
    name
    |> schedError(sc)(function_)(schedStringLiteral(sc)(function_)(codes)(name + "_message"))
    |> schedCompleteLeaf(sc)(task)

// A leaf parks until `socket` is ready in the given way; the step answers 0.
let schedPendLeaf (sc: SchedulerContext) task (wait: TaskWaitKind) socket =
    (let _ =
        wait
        |> taskWaitCode
        |> wordConst(sc.schedI64)
        |> schedStore(sc)(task)(offsetWaitKind)
    in
        let _ = schedStore(sc)(task)(offsetWaitHandle)(socket)
        in
            0
            |> schedConst(sc)
            |> buildRet(sc.schedBuilder))

// An internal `(task) -> status` step function whose body `emitBody(stepFn)(task)` builds from its entry block.
let defineLeafStep module_ (sc: SchedulerContext) name emitBody =
    (let stepType = functionType(sc.schedI64)([sc.schedI64])(1u32)(false)
    in
        let stepFn = addInternalFunction(module_)(name)(stepType)
        in
            let _ =
                "entry"
                |> appendBasicBlock(sc.schedContext)(stepFn)
                |> schedAt(sc)
            in
                let _ =
                    0u32
                    |> getParam(stepFn)
                    |> emitBody(stepFn)
                in (stepFn, stepType))

// Stage 0's `ashes_step_tcp_send_task`: writes what remains of the text (`WaitData0` counts what
// was sent), completing with `Ok(length)` once all of it went, parking while the socket is full.
let emitStepTcpSend (sc: SchedulerContext) stepFn task =
    (let block =
        given (name) -> appendBasicBlock(sc.schedContext)(stepFn)(name)
    in
        let socket = schedWord(sc)(task)(offsetIoArgument0)("tcp_send_socket")
        in
            let sent = schedWord(sc)(task)(offsetWaitData0)("tcp_send_sent")
            in
                match emitStringParts(sc.schedBuilder)(sc.schedI64)(sc.schedPtr)(schedWord(sc)(task)(offsetIoArgument1)("tcp_send_text"))("tcp_send_text") with
                    | (total, bytes) ->
                        let doneBlock = block("tcp_send_done")
                        in
                            let writeBlock = block("tcp_send_write")
                            in
                                let wroteBlock = block("tcp_send_wrote")
                                in
                                    let moreBlock = block("tcp_send_more")
                                    in
                                        let errorBlock = block("tcp_send_error")
                                        in
                                            let pendingBlock = block("tcp_send_pending")
                                            in
                                                let failBlock = block("tcp_send_fail")
                                                in
                                                    let remaining = buildSub(sc.schedBuilder)(total)(sent)("tcp_send_remaining")
                                                    in
                                                        let _ =
                                                            schedIf(sc)(schedEq(sc)(remaining)(schedConst(sc)(0))("tcp_send_nothing_left"))(doneBlock)(writeBlock)
                                                        in
                                                            let _ = schedAt(sc)(doneBlock)
                                                            in
                                                                let _ =
                                                                    "tcp_send_ok"
                                                                    |> schedOk(sc)(stepFn)(total)
                                                                    |> schedCompleteLeaf(sc)(task)
                                                                in
                                                                    let _ = schedAt(sc)(writeBlock)
                                                                    in
                                                                        let written =
                                                                            emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(1))(socket)(buildAdd(sc.schedBuilder)(bytes)(sent)("tcp_send_cursor"))(remaining)("tcp_send_call")
                                                                        in
                                                                            let _ =
                                                                                schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateSgt)(written)(schedConst(sc)(0))("tcp_send_progress"))(wroteBlock)(errorBlock)
                                                                            in
                                                                                let _ = schedAt(sc)(wroteBlock)
                                                                                in
                                                                                    let nextSent = buildAdd(sc.schedBuilder)(sent)(written)("tcp_send_next_sent")
                                                                                    in
                                                                                        let _ = schedStore(sc)(task)(offsetWaitData0)(nextSent)
                                                                                        in
                                                                                            let _ =
                                                                                                schedIf(sc)(schedEq(sc)(nextSent)(total)("tcp_send_all_sent"))(doneBlock)(moreBlock)
                                                                                            in
                                                                                                let _ = schedAt(sc)(moreBlock)
                                                                                                in
                                                                                                    let _ = schedPendLeaf(sc)(task)(SocketWriteTaskWait)(socket)
                                                                                                    in
                                                                                                        let _ = schedAt(sc)(errorBlock)
                                                                                                        in
                                                                                                            let _ =
                                                                                                                schedIf(sc)(schedEq(sc)(written)(schedConst(sc)(-11))("tcp_send_would_block"))(pendingBlock)(failBlock)
                                                                                                            in
                                                                                                                let _ = schedAt(sc)(pendingBlock)
                                                                                                                in
                                                                                                                    let _ = schedPendLeaf(sc)(task)(SocketWriteTaskWait)(socket)
                                                                                                                    in
                                                                                                                        let _ = schedAt(sc)(failBlock)
                                                                                                                        in schedFailLeaf(sc)(stepFn)(task)(tcpSendFailedCodes)("tcp_send_failure"))

// Stage 0's `ashes_step_tcp_receive_task`: reads up to `maxBytes` into a buffer string kept in
// `WaitData0`, completing with `Ok(text)` (empty at end of stream) when the bytes are UTF-8, and
// parking while nothing is available.
let emitStepTcpReceive (sc: SchedulerContext) stepFn task =
    (let block =
        given (name) -> appendBasicBlock(sc.schedContext)(stepFn)(name)
    in
        let socket = schedWord(sc)(task)(offsetIoArgument0)("tcp_receive_socket")
        in
            let maxBytes = schedWord(sc)(task)(offsetIoArgument1)("tcp_receive_max")
            in
                let badMaxBlock = block("tcp_receive_bad_max")
                in
                    let bufferBlock = block("tcp_receive_buffer")
                    in
                        let createBlock = block("tcp_receive_create_buffer")
                        in
                            let readBlock = block("tcp_receive_read")
                            in
                                let readOkBlock = block("tcp_receive_read_ok")
                                in
                                    let validateBlock = block("tcp_receive_validate")
                                    in
                                        let successBlock = block("tcp_receive_success")
                                        in
                                            let invalidBlock = block("tcp_receive_invalid_utf8")
                                            in
                                                let errorBlock = block("tcp_receive_error")
                                                in
                                                    let pendingBlock = block("tcp_receive_pending")
                                                    in
                                                        let failBlock = block("tcp_receive_fail")
                                                        in
                                                            let _ =
                                                                schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateSgt)(maxBytes)(schedConst(sc)(0))("tcp_receive_max_positive"))(bufferBlock)(badMaxBlock)
                                                            in
                                                                let _ = schedAt(sc)(badMaxBlock)
                                                                in
                                                                    let _ = schedFailLeaf(sc)(stepFn)(task)(tcpInvalidMaxBytesCodes)("tcp_receive_bad_max_failure")
                                                                    in
                                                                        let _ = schedAt(sc)(bufferBlock)
                                                                        in
                                                                            let _ =
                                                                                schedIf(sc)(schedEq(sc)(schedWord(sc)(task)(offsetWaitData0)("tcp_receive_existing"))(schedConst(sc)(0))("tcp_receive_needs_buffer"))(createBlock)(readBlock)
                                                                            in
                                                                                let _ = schedAt(sc)(createBlock)
                                                                                in
                                                                                    let buffer =
                                                                                        emitArenaAllocDynamic(sc.schedContext)(stepFn)(sc.schedBuilder)(sc.schedI64)(sc.schedArena)(buildAnd(sc.schedBuilder)(buildAdd(sc.schedBuilder)(maxBytes)(schedConst(sc)(15))("tcp_receive_size_plus"))(schedConst(sc)(-8))("tcp_receive_size"))("tcp_receive_new_buffer")
                                                                                    in
                                                                                        let _ =
                                                                                            0
                                                                                            |> schedConst(sc)
                                                                                            |> schedStore(sc)(buffer)(0)
                                                                                        in
                                                                                            let _ = schedStore(sc)(task)(offsetWaitData0)(buffer)
                                                                                            in
                                                                                                let _ = schedBr(sc)(readBlock)
                                                                                                in
                                                                                                    let _ = schedAt(sc)(readBlock)
                                                                                                    in
                                                                                                        let activeBuffer = schedWord(sc)(task)(offsetWaitData0)("tcp_receive_active_buffer")
                                                                                                        in
                                                                                                            let bufferBytes =
                                                                                                                buildAdd(sc.schedBuilder)(activeBuffer)(schedConst(sc)(8))("tcp_receive_bytes")
                                                                                                            in
                                                                                                                let count =
                                                                                                                    emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(0))(socket)(bufferBytes)(maxBytes)("tcp_receive_call")
                                                                                                                in
                                                                                                                    let _ =
                                                                                                                        schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateSge)(count)(schedConst(sc)(0))("tcp_receive_ok"))(readOkBlock)(errorBlock)
                                                                                                                    in
                                                                                                                        let _ = schedAt(sc)(readOkBlock)
                                                                                                                        in
                                                                                                                            let _ = schedStore(sc)(activeBuffer)(0)(count)
                                                                                                                            in
                                                                                                                                let _ =
                                                                                                                                    schedIf(sc)(schedEq(sc)(count)(schedConst(sc)(0))("tcp_receive_end"))(successBlock)(validateBlock)
                                                                                                                                in
                                                                                                                                    let _ = schedAt(sc)(validateBlock)
                                                                                                                                    in
                                                                                                                                        let valid =
                                                                                                                                            emitValidateUtf8(sc.schedContext)(stepFn)(sc.schedI64)(sc.schedI8)(sc.schedBuilder)(buildIntToPtr(sc.schedBuilder)(bufferBytes)(sc.schedPtr)("tcp_receive_bytes_ptr"))(count)("tcp_receive_utf8")
                                                                                                                                        in
                                                                                                                                            let _ =
                                                                                                                                                schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateNe)(valid)(schedConst(sc)(0))("tcp_receive_valid"))(successBlock)(invalidBlock)
                                                                                                                                            in
                                                                                                                                                let _ = schedAt(sc)(successBlock)
                                                                                                                                                in
                                                                                                                                                    let _ =
                                                                                                                                                        "tcp_receive_result"
                                                                                                                                                        |> schedOk(sc)(stepFn)(activeBuffer)
                                                                                                                                                        |> schedCompleteLeaf(sc)(task)
                                                                                                                                                    in
                                                                                                                                                        let _ = schedAt(sc)(invalidBlock)
                                                                                                                                                        in
                                                                                                                                                            let _ = schedFailLeaf(sc)(stepFn)(task)(tcpInvalidUtf8Codes)("tcp_receive_invalid_failure")
                                                                                                                                                            in
                                                                                                                                                                let _ = schedAt(sc)(errorBlock)
                                                                                                                                                                in
                                                                                                                                                                    let _ =
                                                                                                                                                                        schedIf(sc)(schedEq(sc)(count)(schedConst(sc)(-11))("tcp_receive_would_block"))(pendingBlock)(failBlock)
                                                                                                                                                                    in
                                                                                                                                                                        let _ = schedAt(sc)(pendingBlock)
                                                                                                                                                                        in
                                                                                                                                                                            let _ = schedPendLeaf(sc)(task)(SocketReadTaskWait)(socket)
                                                                                                                                                                            in
                                                                                                                                                                                let _ = schedAt(sc)(failBlock)
                                                                                                                                                                                in schedFailLeaf(sc)(stepFn)(task)(tcpReceiveFailedCodes)("tcp_receive_failure"))

// Stage 0's `ashes_step_tcp_close_task`: closes the socket, `Ok(Unit)` or the close failure.
let emitStepTcpClose (sc: SchedulerContext) stepFn task =
    (let okBlock = appendBasicBlock(sc.schedContext)(stepFn)("tcp_close_ok")
    in
        let failBlock = appendBasicBlock(sc.schedContext)(stepFn)("tcp_close_fail")
        in
            let closed =
                "tcp_close_socket"
                |> schedWord(sc)(task)(offsetIoArgument0)
                |> emitLinuxClose(sc.schedBuilder)(sc.schedI64)
            in
                let _ =
                    schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateSge)(closed)(schedConst(sc)(0))("tcp_close_succeeded"))(okBlock)(failBlock)
                in
                    let _ = schedAt(sc)(okBlock)
                    in
                        let unitValue = emitArenaAllocAdt(sc.schedContext)(stepFn)(sc.schedBuilder)(sc.schedI64)(sc.schedI8)(sc.schedPtr)(sc.schedArena)(0)(0)(false)("tcp_close_unit")
                        in
                            let _ =
                                "tcp_close_result"
                                |> schedOk(sc)(stepFn)(unitValue)
                                |> schedCompleteLeaf(sc)(task)
                            in
                                let _ = schedAt(sc)(failBlock)
                                in schedFailLeaf(sc)(stepFn)(task)(tcpCloseFailedCodes)("tcp_close_failure"))

// Stage 0's `ashes_step_tcp_listen_task` for Linux: a socket with `SO_REUSEADDR` and
// `SO_REUSEPORT`, bound to `INADDR_ANY:port`, listening with a backlog of 128 and non-blocking,
// completing with `Ok(socket)`; it never parks.
let emitStepTcpListen (sc: SchedulerContext) stepFn task =
    (let block =
        given (name) -> appendBasicBlock(sc.schedContext)(stepFn)(name)
    in
        let optionWord = buildAlloca(sc.schedBuilder)(sc.schedI64)("tcp_listen_option")
        in
            let sockaddr =
                buildAlloca(sc.schedBuilder)(arrayType(sc.schedI64)(2u64))("tcp_listen_sockaddr")
            in
                let socketBlock = block("tcp_listen_socket")
                in
                    let bindBlock = block("tcp_listen_bound")
                    in
                        let successBlock = block("tcp_listen_success")
                        in
                            let failBlock = block("tcp_listen_fail")
                            in
                                let port = schedWord(sc)(task)(offsetIoArgument0)("tcp_listen_port")
                                in
                                    let socket =
                                        emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(41))(schedConst(sc)(2))(schedConst(sc)(1))(schedConst(sc)(0))("tcp_listen_socket_call")
                                    in
                                        let _ =
                                            schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateSge)(socket)(schedConst(sc)(0))("tcp_listen_socket_ok"))(socketBlock)(failBlock)
                                        in
                                            let _ = schedAt(sc)(socketBlock)
                                            in
                                                let optionAddress = buildPtrToInt(sc.schedBuilder)(optionWord)(sc.schedI64)("tcp_listen_option_address")
                                                in
                                                    let _ =
                                                        1
                                                        |> schedConst(sc)
                                                        |> schedStore(sc)(optionAddress)(0)
                                                    in
                                                        let _ =
                                                            emitLinuxSyscallCall6(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(54))(socket)(schedConst(sc)(1))(schedConst(sc)(2))(optionAddress)(schedConst(sc)(4))(schedConst(sc)(0))("tcp_listen_reuse_address")
                                                        in
                                                            let _ =
                                                                emitLinuxSyscallCall6(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(54))(socket)(schedConst(sc)(1))(schedConst(sc)(15))(optionAddress)(schedConst(sc)(4))(schedConst(sc)(0))("tcp_listen_reuse_port")
                                                            in
                                                                let portNetwork =
                                                                    buildOr(sc.schedBuilder)(buildMul(sc.schedBuilder)(buildAnd(sc.schedBuilder)(port)(schedConst(sc)(255))("tcp_listen_port_low"))(schedConst(sc)(256))("tcp_listen_port_low_shifted"))(buildAnd(sc.schedBuilder)(buildSDiv(sc.schedBuilder)(port)(schedConst(sc)(256))("tcp_listen_port_high"))(schedConst(sc)(255))("tcp_listen_port_high_masked"))("tcp_listen_port_network")
                                                                in
                                                                    let sockaddrAddress = buildPtrToInt(sc.schedBuilder)(sockaddr)(sc.schedI64)("tcp_listen_sockaddr_address")
                                                                    in
                                                                        let _ =
                                                                            "tcp_listen_sockaddr_word"
                                                                            |> buildOr(sc.schedBuilder)(schedConst(sc)(2))(buildShl(sc.schedBuilder)(portNetwork)(schedConst(sc)(16))("tcp_listen_port_field"))
                                                                            |> schedStore(sc)(sockaddrAddress)(0)
                                                                        in
                                                                            let _ =
                                                                                0
                                                                                |> schedConst(sc)
                                                                                |> schedStore(sc)(sockaddrAddress)(8)
                                                                            in
                                                                                let bound =
                                                                                    emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(49))(socket)(sockaddrAddress)(schedConst(sc)(16))("tcp_listen_bind")
                                                                                in
                                                                                    let _ =
                                                                                        schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateSge)(bound)(schedConst(sc)(0))("tcp_listen_bind_ok"))(bindBlock)(failBlock)
                                                                                    in
                                                                                        let _ = schedAt(sc)(bindBlock)
                                                                                        in
                                                                                            let listening =
                                                                                                emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(50))(socket)(schedConst(sc)(128))(schedConst(sc)(0))("tcp_listen_listen")
                                                                                            in
                                                                                                let _ =
                                                                                                    schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateSge)(listening)(schedConst(sc)(0))("tcp_listen_listen_ok"))(successBlock)(failBlock)
                                                                                                in
                                                                                                    let _ = schedAt(sc)(successBlock)
                                                                                                    in
                                                                                                        let flags =
                                                                                                            emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(72))(socket)(schedConst(sc)(3))(schedConst(sc)(0))("tcp_listen_get_flags")
                                                                                                        in
                                                                                                            let _ =
                                                                                                                emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(72))(socket)(schedConst(sc)(4))(buildOr(sc.schedBuilder)(flags)(schedConst(sc)(2048))("tcp_listen_nonblocking_flags"))("tcp_listen_set_flags")
                                                                                                            in
                                                                                                                let _ =
                                                                                                                    "tcp_listen_result"
                                                                                                                    |> schedOk(sc)(stepFn)(socket)
                                                                                                                    |> schedCompleteLeaf(sc)(task)
                                                                                                                in
                                                                                                                    let _ = schedAt(sc)(failBlock)
                                                                                                                    in schedFailLeaf(sc)(stepFn)(task)(tcpListenFailedCodes)("tcp_listen_failure"))

// Stage 0's `ashes_step_tcp_accept_task` without the graceful-shutdown drain: a non-blocking
// `accept4` completing with `Ok(client)`, parking on the listener while no connection waits.
let emitStepTcpAccept (sc: SchedulerContext) stepFn task =
    (let acceptedBlock = appendBasicBlock(sc.schedContext)(stepFn)("tcp_accept_accepted")
    in
        let errorBlock = appendBasicBlock(sc.schedContext)(stepFn)("tcp_accept_error")
        in
            let pendingBlock = appendBasicBlock(sc.schedContext)(stepFn)("tcp_accept_pending")
            in
                let failBlock = appendBasicBlock(sc.schedContext)(stepFn)("tcp_accept_fail")
                in
                    let listener = schedWord(sc)(task)(offsetIoArgument0)("tcp_accept_listener")
                    in
                        let client =
                            emitLinuxSyscallCall4(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(288))(listener)(schedConst(sc)(0))(schedConst(sc)(0))(schedConst(sc)(2048))("tcp_accept_call")
                        in
                            let _ =
                                schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateSge)(client)(schedConst(sc)(0))("tcp_accept_ok"))(acceptedBlock)(errorBlock)
                            in
                                let _ = schedAt(sc)(acceptedBlock)
                                in
                                    let _ =
                                        "tcp_accept_result"
                                        |> schedOk(sc)(stepFn)(client)
                                        |> schedCompleteLeaf(sc)(task)
                                    in
                                        let _ = schedAt(sc)(errorBlock)
                                        in
                                            let _ =
                                                schedIf(sc)(schedEq(sc)(client)(schedConst(sc)(-11))("tcp_accept_would_block"))(pendingBlock)(failBlock)
                                            in
                                                let _ = schedAt(sc)(pendingBlock)
                                                in
                                                    let _ = schedPendLeaf(sc)(task)(SocketReadTaskWait)(listener)
                                                    in
                                                        let _ = schedAt(sc)(failBlock)
                                                        in schedFailLeaf(sc)(stepFn)(task)(tcpAcceptFailedCodes)("tcp_accept_failure"))

// Stage 0's `EmitStepComposite` for `all` and `race`. The first step schedules every child with the
// composite as its waiter and counts them (`all` waits for that many completions, `race` for one);
// an empty list completes at once, `all` with `Ok([])`. The step after the children finish
// completes the composite: `race` already holds its winner's result, `all` collects, the first
// failed child's result in list order or `Ok` of the children's values in order. Answers 1 when
// the composite completed.
let defineStepComposite module_ (sc: SchedulerContext) scopeFn scopeType =
    (let stepType = functionType(sc.schedI64)([sc.schedI64])(1u32)(false)
    in
        let stepFn = addInternalFunction(module_)("__ashes_step_composite")(stepType)
        in
            let block =
                given (name) -> appendBasicBlock(sc.schedContext)(stepFn)(name)
            in
                let entryBlock = block("entry")
                in
                    let enqueueLoop = block("composite_enqueue_loop")
                    in
                        let enqueueBody = block("composite_enqueue_body")
                        in
                            let afterEnqueue = block("composite_after_enqueue")
                            in
                                let emptyBlock = block("composite_empty")
                                in
                                    let pendingBlock = block("composite_pending")
                                    in
                                        let finishBlock = block("composite_finish")
                                        in
                                            let collectBlock = block("composite_collect")
                                            in
                                                let failLoop = block("composite_fail_loop")
                                                in
                                                    let failBody = block("composite_fail_body")
                                                    in
                                                        let failedBlock = block("composite_failed")
                                                        in
                                                            let buildLoop = block("composite_build_loop")
                                                            in
                                                                let buildBody = block("composite_build_body")
                                                                in
                                                                    let reverseLoop = block("composite_reverse_loop")
                                                                    in
                                                                        let reverseBody = block("composite_reverse_body")
                                                                        in
                                                                            let succeededBlock = block("composite_succeeded")
                                                                            in
                                                                                let completeBlock = block("composite_complete")
                                                                                in
                                                                                    let task = getParam(stepFn)(0u32)
                                                                                    in
                                                                                        let _ = schedAt(sc)(entryBlock)
                                                                                        in
                                                                                            let cursorSlot = schedSlot(sc)("composite_cursor")
                                                                                            in
                                                                                                let countSlot = schedSlot(sc)("composite_count")
                                                                                                in
                                                                                                    let listSlot = schedSlot(sc)("composite_list")
                                                                                                    in
                                                                                                        let scopeBlock = appendBasicBlock(sc.schedContext)(stepFn)("composite_scope")
                                                                                                        in
                                                                                                            let listCompositeBlock = appendBasicBlock(sc.schedContext)(stepFn)("composite_list_kind")
                                                                                                            in
                                                                                                                let _ =
                                                                                                                    schedIf(sc)(schedEq(sc)(schedWord(sc)(task)(offsetStateIndex)("composite_kind"))(stateConst(sc.schedI64)(ScopeCompositeTaskState))("composite_is_scope"))(scopeBlock)(listCompositeBlock)
                                                                                                                in
                                                                                                                    let _ = schedAt(sc)(scopeBlock)
                                                                                                                    in
                                                                                                                        let _ =
                                                                                                                            "composite_scope_status"
                                                                                                                            |> buildCall(sc.schedBuilder)(scopeType)(scopeFn)([task])(1u32)
                                                                                                                            |> buildRet(sc.schedBuilder)
                                                                                                                        in
                                                                                                                            let _ = schedAt(sc)(listCompositeBlock)
                                                                                                                            in
                                                                                                                                let isRace =
                                                                                                                                    schedEq(sc)(schedWord(sc)(task)(offsetStateIndex)("composite_state"))(stateConst(sc.schedI64)(RaceCompositeTaskState))("composite_is_race")
                                                                                                                                in
                                                                                                                                    let children = schedWord(sc)(task)(offsetIoArgument0)("composite_children")
                                                                                                                                    in
                                                                                                                                        let _ =
                                                                                                                                            schedIf(sc)(schedEq(sc)(schedWord(sc)(task)(offsetIoArgument1)("composite_phase"))(schedConst(sc)(0))("composite_is_first_step"))(enqueueLoop)(finishBlock)
                                                                                                                                        in
                                                                                                                                            let _ = schedAt(sc)(enqueueLoop)
                                                                                                                                            in
                                                                                                                                                let _ =
                                                                                                                                                    1
                                                                                                                                                    |> schedConst(sc)
                                                                                                                                                    |> schedStore(sc)(task)(offsetIoArgument1)
                                                                                                                                                in
                                                                                                                                                    let _ = schedSet(sc)(cursorSlot)(children)
                                                                                                                                                    in
                                                                                                                                                        let _ =
                                                                                                                                                            0
                                                                                                                                                            |> schedConst(sc)
                                                                                                                                                            |> schedSet(sc)(countSlot)
                                                                                                                                                        in
                                                                                                                                                            let enqueueCheck = block("composite_enqueue_check")
                                                                                                                                                            in
                                                                                                                                                                let _ = schedBr(sc)(enqueueCheck)
                                                                                                                                                                in
                                                                                                                                                                    let _ = schedAt(sc)(enqueueCheck)
                                                                                                                                                                    in
                                                                                                                                                                        let cursor = schedLoad(sc)(cursorSlot)("composite_enqueue_cursor")
                                                                                                                                                                        in
                                                                                                                                                                            let _ =
                                                                                                                                                                                schedIf(sc)(schedEq(sc)(cursor)(schedConst(sc)(0))("composite_enqueue_end"))(afterEnqueue)(enqueueBody)
                                                                                                                                                                            in
                                                                                                                                                                                let _ = schedAt(sc)(enqueueBody)
                                                                                                                                                                                in
                                                                                                                                                                                    let child = schedWord(sc)(cursor)(0)("composite_child")
                                                                                                                                                                                    in
                                                                                                                                                                                        let _ =
                                                                                                                                                                                            "composite_child_tail"
                                                                                                                                                                                            |> schedWord(sc)(cursor)(8)
                                                                                                                                                                                            |> schedSet(sc)(cursorSlot)
                                                                                                                                                                                        in
                                                                                                                                                                                            let _ = schedStore(sc)(child)(offsetWaiter)(task)
                                                                                                                                                                                            in
                                                                                                                                                                                                let _ =
                                                                                                                                                                                                    "composite_owner"
                                                                                                                                                                                                    |> schedWord(sc)(task)(offsetArenaOwner)
                                                                                                                                                                                                    |> schedStore(sc)(child)(offsetArenaOwner)
                                                                                                                                                                                                in
                                                                                                                                                                                                    let _ = schedEnqueue(sc)(child)
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                            "composite_count_next"
                                                                                                                                                                                                            |> buildAdd(sc.schedBuilder)(schedLoad(sc)(countSlot)("composite_count_now"))(schedConst(sc)(1))
                                                                                                                                                                                                            |> schedSet(sc)(countSlot)
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let _ = schedBr(sc)(enqueueCheck)
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let _ = schedAt(sc)(afterEnqueue)
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let count = schedLoad(sc)(countSlot)("composite_final_count")
                                                                                                                                                                                                                    in
                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                            "composite_counter"
                                                                                                                                                                                                                            |> buildSelect(sc.schedBuilder)(isRace)(schedConst(sc)(0))(count)
                                                                                                                                                                                                                            |> schedStore(sc)(task)(offsetWaitData0)
                                                                                                                                                                                                                        in
                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                schedIf(sc)(schedEq(sc)(count)(schedConst(sc)(0))("composite_no_children"))(emptyBlock)(pendingBlock)
                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                let _ = schedAt(sc)(pendingBlock)
                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                        0
                                                                                                                                                                                                                                        |> schedConst(sc)
                                                                                                                                                                                                                                        |> buildRet(sc.schedBuilder)
                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                        let _ = schedAt(sc)(emptyBlock)
                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                "composite_empty_result"
                                                                                                                                                                                                                                                |> buildSelect(sc.schedBuilder)(isRace)(schedConst(sc)(0))(schedOk(sc)(stepFn)(schedConst(sc)(0))("composite_empty_ok"))
                                                                                                                                                                                                                                                |> schedStore(sc)(task)(offsetResultSlot)
                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                let _ = schedBr(sc)(completeBlock)
                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                    let _ = schedAt(sc)(finishBlock)
                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                        let _ = schedIf(sc)(isRace)(completeBlock)(collectBlock)
                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                            let _ = schedAt(sc)(collectBlock)
                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                let _ = schedSet(sc)(cursorSlot)(children)
                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                    let _ = schedBr(sc)(failLoop)
                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                        let _ = schedAt(sc)(failLoop)
                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                            let failCursor = schedLoad(sc)(cursorSlot)("composite_fail_cursor")
                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                                                                    schedIf(sc)(schedEq(sc)(failCursor)(schedConst(sc)(0))("composite_fail_end"))(buildLoop)(failBody)
                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                    let _ = schedAt(sc)(failBody)
                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                        let childResult =
                                                                                                                                                                                                                                                                                            schedWord(sc)(schedWord(sc)(failCursor)(0)("composite_fail_child"))(offsetResultSlot)("composite_fail_result")
                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                "composite_fail_tail"
                                                                                                                                                                                                                                                                                                |> schedWord(sc)(failCursor)(8)
                                                                                                                                                                                                                                                                                                |> schedSet(sc)(cursorSlot)
                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                let _ = schedSet(sc)(listSlot)(childResult)
                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                                                                        schedIf(sc)(schedEq(sc)(schedWord(sc)(childResult)(0)("composite_fail_tag"))(schedConst(sc)(0))("composite_child_ok"))(failLoop)(failedBlock)
                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                        let _ = schedAt(sc)(failedBlock)
                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                "composite_failure"
                                                                                                                                                                                                                                                                                                                |> schedLoad(sc)(listSlot)
                                                                                                                                                                                                                                                                                                                |> schedStore(sc)(task)(offsetResultSlot)
                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                let _ = schedBr(sc)(completeBlock)
                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                    let _ = schedAt(sc)(buildLoop)
                                                                                                                                                                                                                                                                                                                    in emitCompositeCollect(sc)(stepFn)(task)(children)(cursorSlot)(listSlot)(buildLoop)(buildBody)(reverseLoop)(reverseBody)(succeededBlock)(completeBlock)(stepType))

// Milliseconds on the monotonic clock.
let emitMonotonicMillis (sc: SchedulerContext) timespec name =
    (let address = buildPtrToInt(sc.schedBuilder)(timespec)(sc.schedI64)(name + "_timespec")
    in
        let _ =
            emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(228))(schedConst(sc)(1))(address)(schedConst(sc)(0))(name + "_clock")
        in
            buildAdd(sc.schedBuilder)(buildMul(sc.schedBuilder)(schedWord(sc)(address)(0)(name + "_seconds"))(schedConst(sc)(1000))(name + "_seconds_ms"))(buildSDiv(sc.schedBuilder)(schedWord(sc)(address)(8)(name + "_nanoseconds"))(schedConst(sc)(1000000))(name + "_nanoseconds_ms"))(name))

// Stage 0's aggregate wait for Linux: the earliest timer among the parked leaves bounds the wait;
// with a socket leaf parked, its socket is registered in the scheduler's persistent epoll set
// (readable for a read wait, writable for a write wait) and the wait is one `epoll_wait`,
// otherwise a sleep. Every parked timer is counted down by the time waited, and every parked leaf
// goes back on the run queue.
let defineWaitParked module_ (sc: SchedulerContext) =
    (let waitType = functionType(sc.schedI64)([])(0u32)(false)
    in
        let waitFn = addInternalFunction(module_)("__ashes_wait_parked")(waitType)
        in
            let block =
                given (name) -> appendBasicBlock(sc.schedContext)(waitFn)(name)
            in
                let entryBlock = block("entry")
                in
                    let scanCheck = block("wait_scan_check")
                    in
                        let scanBody = block("wait_scan_body")
                        in
                            let scanTimer = block("wait_scan_timer")
                            in
                                let scanOther = block("wait_scan_other")
                                in
                                    let scanSocket = block("wait_scan_socket")
                                    in
                                        let createEpollBlock = block("wait_create_epoll")
                                        in
                                            let registerBlock = block("wait_register")
                                            in
                                                let modifyBlock = block("wait_modify")
                                                in
                                                    let decideBlock = block("wait_decide")
                                                    in
                                                        let epollBlock = block("wait_epoll")
                                                        in
                                                            let sleepBlock = block("wait_sleep")
                                                            in
                                                                let requeueStart = block("wait_requeue_start")
                                                                in
                                                                    let requeueCheck = block("wait_requeue_check")
                                                                    in
                                                                        let requeueBody = block("wait_requeue_body")
                                                                        in
                                                                            let requeueTimer = block("wait_requeue_timer")
                                                                            in
                                                                                let requeueEnqueue = block("wait_requeue_enqueue")
                                                                                in
                                                                                    let doneBlock = block("wait_done")
                                                                                    in
                                                                                        let maxWord = constInt(sc.schedI64)(9223372036854775807u64)(false)
                                                                                        in
                                                                                            let timerCode =
                                                                                                TimerTaskWait
                                                                                                |> taskWaitCode
                                                                                                |> wordConst(sc.schedI64)
                                                                                            in
                                                                                                let _ = schedAt(sc)(entryBlock)
                                                                                                in
                                                                                                    let minSlot = schedSlot(sc)("wait_min")
                                                                                                    in
                                                                                                        let cursorSlot = schedSlot(sc)("wait_cursor")
                                                                                                        in
                                                                                                            let socketSlot = schedSlot(sc)("wait_has_socket")
                                                                                                            in
                                                                                                                let elapsedSlot = schedSlot(sc)("wait_elapsed")
                                                                                                                in
                                                                                                                    let timespec =
                                                                                                                        buildAlloca(sc.schedBuilder)(arrayType(sc.schedI64)(2u64))("wait_timespec")
                                                                                                                    in
                                                                                                                        let eventStorage =
                                                                                                                            buildAlloca(sc.schedBuilder)(arrayType(sc.schedI64)(2u64))("wait_event")
                                                                                                                        in
                                                                                                                            let eventAddress = buildPtrToInt(sc.schedBuilder)(eventStorage)(sc.schedI64)("wait_event_address")
                                                                                                                            in
                                                                                                                                let _ = schedSet(sc)(minSlot)(maxWord)
                                                                                                                                in
                                                                                                                                    let _ =
                                                                                                                                        0
                                                                                                                                        |> schedConst(sc)
                                                                                                                                        |> schedSet(sc)(socketSlot)
                                                                                                                                    in
                                                                                                                                        let _ =
                                                                                                                                            "wait_parked"
                                                                                                                                            |> schedLoad(sc)(parkedHeadGlobal(sc))
                                                                                                                                            |> schedSet(sc)(cursorSlot)
                                                                                                                                        in
                                                                                                                                            let _ = schedBr(sc)(scanCheck)
                                                                                                                                            in
                                                                                                                                                let _ = schedAt(sc)(scanCheck)
                                                                                                                                                in
                                                                                                                                                    let scanCursor = schedLoad(sc)(cursorSlot)("wait_scan_cursor")
                                                                                                                                                    in
                                                                                                                                                        let _ =
                                                                                                                                                            schedIf(sc)(schedEq(sc)(scanCursor)(schedConst(sc)(0))("wait_scan_end"))(decideBlock)(scanBody)
                                                                                                                                                        in
                                                                                                                                                            let _ = schedAt(sc)(scanBody)
                                                                                                                                                            in
                                                                                                                                                                let _ =
                                                                                                                                                                    "wait_scan_next"
                                                                                                                                                                    |> schedWord(sc)(scanCursor)(offsetReadyNext)
                                                                                                                                                                    |> schedSet(sc)(cursorSlot)
                                                                                                                                                                in
                                                                                                                                                                    let kind = schedWord(sc)(scanCursor)(offsetWaitKind)("wait_scan_kind")
                                                                                                                                                                    in
                                                                                                                                                                        let _ =
                                                                                                                                                                            schedIf(sc)(schedEq(sc)(kind)(timerCode)("wait_scan_is_timer"))(scanTimer)(scanOther)
                                                                                                                                                                        in
                                                                                                                                                                            let _ = schedAt(sc)(scanTimer)
                                                                                                                                                                            in
                                                                                                                                                                                let remaining = schedWord(sc)(scanCursor)(offsetSleepDurationMilliseconds)("wait_remaining")
                                                                                                                                                                                in
                                                                                                                                                                                    let currentMin = schedLoad(sc)(minSlot)("wait_current_min")
                                                                                                                                                                                    in
                                                                                                                                                                                        let _ =
                                                                                                                                                                                            "wait_new_min"
                                                                                                                                                                                            |> buildSelect(sc.schedBuilder)(buildICmp(sc.schedBuilder)(intPredicateSlt)(remaining)(currentMin)("wait_is_less"))(remaining)(currentMin)
                                                                                                                                                                                            |> schedSet(sc)(minSlot)
                                                                                                                                                                                        in
                                                                                                                                                                                            let _ = schedBr(sc)(scanCheck)
                                                                                                                                                                                            in
                                                                                                                                                                                                let _ = schedAt(sc)(scanOther)
                                                                                                                                                                                                in
                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                        schedIf(sc)(schedEq(sc)(kind)(schedConst(sc)(0))("wait_scan_no_wait"))(scanCheck)(scanSocket)
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let _ = schedAt(sc)(scanSocket)
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                1
                                                                                                                                                                                                                |> schedConst(sc)
                                                                                                                                                                                                                |> schedSet(sc)(socketSlot)
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                    schedIf(sc)(schedEq(sc)(schedLoad(sc)(epollGlobal(sc))("wait_epoll_existing"))(schedConst(sc)(0))("wait_epoll_missing"))(createEpollBlock)(registerBlock)
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let _ = schedAt(sc)(createEpollBlock)
                                                                                                                                                                                                                    in
                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                            "wait_epoll_create"
                                                                                                                                                                                                                            |> emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(291))(schedConst(sc)(0))(schedConst(sc)(0))(schedConst(sc)(0))
                                                                                                                                                                                                                            |> schedSet(sc)(epollGlobal(sc))
                                                                                                                                                                                                                        in
                                                                                                                                                                                                                            let _ = schedBr(sc)(registerBlock)
                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                let _ = schedAt(sc)(registerBlock)
                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                    let readish =
                                                                                                                                                                                                                                        buildOr(sc.schedBuilder)(schedEq(sc)(kind)(SocketReadTaskWait
                                                                                                                                                                                                                                        |> taskWaitCode
                                                                                                                                                                                                                                        |> wordConst(sc.schedI64))("wait_is_read"))(schedEq(sc)(kind)(TlsReadTaskWait
                                                                                                                                                                                                                                        |> taskWaitCode
                                                                                                                                                                                                                                        |> wordConst(sc.schedI64))("wait_is_tls_read"))("wait_readish")
                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                        let mask =
                                                                                                                                                                                                                                            buildSelect(sc.schedBuilder)(readish)(schedConst(sc)(1))(schedConst(sc)(4))("wait_mask")
                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                            let socket = schedWord(sc)(scanCursor)(offsetWaitHandle)("wait_socket")
                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                let _ = schedStore(sc)(eventAddress)(0)(mask)
                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                    let _ = schedStore(sc)(eventAddress)(4)(socket)
                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                        let epollFd =
                                                                                                                                                                                                                                                            schedLoad(sc)(epollGlobal(sc))("wait_epoll_fd")
                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                            let added =
                                                                                                                                                                                                                                                                emitLinuxSyscallCall4(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(233))(epollFd)(schedConst(sc)(1))(socket)(eventAddress)("wait_epoll_add")
                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                                                    schedIf(sc)(schedEq(sc)(added)(schedConst(sc)(-17))("wait_epoll_exists"))(modifyBlock)(scanCheck)
                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                    let _ = schedAt(sc)(modifyBlock)
                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                                            emitLinuxSyscallCall4(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(233))(epollFd)(schedConst(sc)(3))(socket)(eventAddress)("wait_epoll_modify")
                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                            let _ = schedBr(sc)(scanCheck)
                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                let _ = schedAt(sc)(decideBlock)
                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                    let minimum = schedLoad(sc)(minSlot)("wait_minimum")
                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                        let noTimer = schedEq(sc)(minimum)(maxWord)("wait_no_timer")
                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                schedIf(sc)(schedEq(sc)(schedLoad(sc)(socketSlot)("wait_socket_flag"))(schedConst(sc)(0))("wait_no_socket"))(sleepBlock)(epollBlock)
                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                let _ = schedAt(sc)(epollBlock)
                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                    let started = emitMonotonicMillis(sc)(timespec)("wait_started")
                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                        let timeout =
                                                                                                                                                                                                                                                                                                            buildSelect(sc.schedBuilder)(noTimer)(schedConst(sc)(-1))(minimum)("wait_epoll_timeout")
                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                emitLinuxSyscallCall4(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(232))(schedLoad(sc)(epollGlobal(sc))("wait_epoll_wait_fd"))(eventAddress)(schedConst(sc)(1))(timeout)("wait_epoll_wait")
                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                                                                                                    "wait_epoll_elapsed"
                                                                                                                                                                                                                                                                                                                    |> buildSub(sc.schedBuilder)(emitMonotonicMillis(sc)(timespec)("wait_finished"))(started)
                                                                                                                                                                                                                                                                                                                    |> schedSet(sc)(elapsedSlot)
                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                    let _ = schedBr(sc)(requeueStart)
                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                        let _ = schedAt(sc)(sleepBlock)
                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                            let sleepMs =
                                                                                                                                                                                                                                                                                                                                buildSelect(sc.schedBuilder)(noTimer)(schedConst(sc)(0))(minimum)("wait_sleep_ms")
                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                let timespecAddress = buildPtrToInt(sc.schedBuilder)(timespec)(sc.schedI64)("wait_sleep_timespec")
                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                                                                                                        "wait_seconds"
                                                                                                                                                                                                                                                                                                                                        |> buildSDiv(sc.schedBuilder)(sleepMs)(schedConst(sc)(1000))
                                                                                                                                                                                                                                                                                                                                        |> schedStore(sc)(timespecAddress)(0)
                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                                                                                                            "wait_nanoseconds"
                                                                                                                                                                                                                                                                                                                                            |> buildMul(sc.schedBuilder)(buildSRem(sc.schedBuilder)(sleepMs)(schedConst(sc)(1000))("wait_ms_rem"))(schedConst(sc)(1000000))
                                                                                                                                                                                                                                                                                                                                            |> schedStore(sc)(timespecAddress)(8)
                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                                                emitLinuxSyscallCall(sc.schedBuilder)(sc.schedI64)(schedConst(sc)(35))(timespecAddress)(schedConst(sc)(0))(schedConst(sc)(0))("wait_nanosleep")
                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                let _ = schedSet(sc)(elapsedSlot)(sleepMs)
                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                    let _ = schedBr(sc)(requeueStart)
                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                        let _ = schedAt(sc)(requeueStart)
                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                            let elapsed = schedLoad(sc)(elapsedSlot)("wait_elapsed_value")
                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                                                                                                                                                    "wait_parked_again"
                                                                                                                                                                                                                                                                                                                                                                    |> schedLoad(sc)(parkedHeadGlobal(sc))
                                                                                                                                                                                                                                                                                                                                                                    |> schedSet(sc)(cursorSlot)
                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                    let _ = schedBr(sc)(requeueCheck)
                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                        let _ = schedAt(sc)(requeueCheck)
                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                            let requeueCursor = schedLoad(sc)(cursorSlot)("wait_requeue_cursor")
                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                                                                                                                                                                    schedIf(sc)(schedEq(sc)(requeueCursor)(schedConst(sc)(0))("wait_requeue_end"))(doneBlock)(requeueBody)
                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                    let _ = schedAt(sc)(requeueBody)
                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                                                                                                                                                            "wait_requeue_next"
                                                                                                                                                                                                                                                                                                                                                                                            |> schedWord(sc)(requeueCursor)(offsetReadyNext)
                                                                                                                                                                                                                                                                                                                                                                                            |> schedSet(sc)(cursorSlot)
                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                                                                                                schedIf(sc)(schedEq(sc)(schedWord(sc)(requeueCursor)(offsetWaitKind)("wait_requeue_kind"))(timerCode)("wait_requeue_is_timer"))(requeueTimer)(requeueEnqueue)
                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                let _ = schedAt(sc)(requeueTimer)
                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                                                                                                                                                                        "wait_requeue_left"
                                                                                                                                                                                                                                                                                                                                                                                                        |> buildSub(sc.schedBuilder)(schedWord(sc)(requeueCursor)(offsetSleepDurationMilliseconds)("wait_requeue_remaining"))(elapsed)
                                                                                                                                                                                                                                                                                                                                                                                                        |> schedStore(sc)(requeueCursor)(offsetSleepDurationMilliseconds)
                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                        let _ = schedBr(sc)(requeueEnqueue)
                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                            let _ = schedAt(sc)(requeueEnqueue)
                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                let _ = schedEnqueue(sc)(requeueCursor)
                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = schedBr(sc)(requeueCheck)
                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = schedAt(sc)(doneBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                                                                                                                                0
                                                                                                                                                                                                                                                                                                                                                                                                                                |> schedConst(sc)
                                                                                                                                                                                                                                                                                                                                                                                                                                |> schedSet(sc)(parkedHeadGlobal(sc))
                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                                                                                                                                                                                                                    0
                                                                                                                                                                                                                                                                                                                                                                                                                                    |> schedConst(sc)
                                                                                                                                                                                                                                                                                                                                                                                                                                    |> buildRet(sc.schedBuilder)
                                                                                                                                                                                                                                                                                                                                                                                                                                in (waitFn, waitType))

type SchedulerFunctions =
    | stepCompositeFn: LLVMValueRef
    | stepCompositeType: LLVMTypeRef
    | waitParkedFn: LLVMValueRef
    | waitParkedType: LLVMTypeRef
    | cancelFn: LLVMValueRef
    | cancelType: LLVMTypeRef
    | dequeueFn: LLVMValueRef
    | dequeueType: LLVMTypeRef
    | forkScopedFn: LLVMValueRef
    | forkScopedType: LLVMTypeRef
    // The socket and HTTP leaves stepped here, by state code.
    | socketLeafSteps: List((Int, LLVMValueRef, LLVMTypeRef))

// Cancels every child of a resolved race but its winner.
let emitCancelRaceLosers (sc: SchedulerContext) (fns: SchedulerFunctions) runFn cursorSlot race winner =
    (let check = appendBasicBlock(sc.schedContext)(runFn)("race_cancel_check")
    in
        let body = appendBasicBlock(sc.schedContext)(runFn)("race_cancel_body")
        in
            let cancelOne = appendBasicBlock(sc.schedContext)(runFn)("race_cancel_one")
            in
                let done = appendBasicBlock(sc.schedContext)(runFn)("race_cancel_done")
                in
                    let _ =
                        "race_cancel_children"
                        |> schedWord(sc)(race)(offsetIoArgument0)
                        |> schedSet(sc)(cursorSlot)
                    in
                        let _ = schedBr(sc)(check)
                        in
                            let _ = schedAt(sc)(check)
                            in
                                let cursor = schedLoad(sc)(cursorSlot)("race_cancel_cursor")
                                in
                                    let _ =
                                        schedIf(sc)(schedEq(sc)(cursor)(schedConst(sc)(0))("race_cancel_end"))(done)(body)
                                    in
                                        let _ = schedAt(sc)(body)
                                        in
                                            let candidate = schedWord(sc)(cursor)(0)("race_cancel_candidate")
                                            in
                                                let _ =
                                                    "race_cancel_next"
                                                    |> schedWord(sc)(cursor)(8)
                                                    |> schedSet(sc)(cursorSlot)
                                                in
                                                    let _ =
                                                        schedIf(sc)(schedEq(sc)(candidate)(winner)("race_cancel_is_winner"))(check)(cancelOne)
                                                    in
                                                        let _ = schedAt(sc)(cancelOne)
                                                        in
                                                            let _ = buildCall(sc.schedBuilder)(fns.cancelType)(fns.cancelFn)([candidate])(1u32)("")
                                                            in
                                                                let _ = schedBr(sc)(check)
                                                                in schedAt(sc)(done))

type SchedulerBlocks =
    | schedLoopBlock: LLVMBasicBlockRef
    | schedEmptyBlock: LLVMBasicBlockRef
    | schedWaitBlock: LLVMBasicBlockRef
    | schedHaveBlock: LLVMBasicBlockRef
    | schedNotDoneBlock: LLVMBasicBlockRef
    | schedCompositeBlock: LLVMBasicBlockRef
    | schedLeafOrCoroutineBlock: LLVMBasicBlockRef
    | schedLeafBlock: LLVMBasicBlockRef
    | schedLeafDoneBlock: LLVMBasicBlockRef
    | schedLeafParkBlock: LLVMBasicBlockRef
    | schedCoroutineBlock: LLVMBasicBlockRef
    | schedSuspendBlock: LLVMBasicBlockRef
    | schedCompleteBlock: LLVMBasicBlockRef
    | schedDeliverBlock: LLVMBasicBlockRef
    | schedAllWaiterBlock: LLVMBasicBlockRef
    | schedNotAllWaiterBlock: LLVMBasicBlockRef
    | schedRaceWaiterBlock: LLVMBasicBlockRef
    | schedRaceFirstBlock: LLVMBasicBlockRef
    | schedEnqueueWaiterBlock: LLVMBasicBlockRef
    | schedNormalWaiterBlock: LLVMBasicBlockRef
    | schedReturnBlock: LLVMBasicBlockRef
    | schedDrainBlock: LLVMBasicBlockRef
    | schedFinishBlock: LLVMBasicBlockRef

let schedulerBlocks context runFn =
    (let block =
        given (name) -> appendBasicBlock(context)(runFn)(name)
    in
        SchedulerBlocks(
            schedLoopBlock = block("sched_loop"),
            schedEmptyBlock = block("sched_empty"),
            schedWaitBlock = block("sched_wait"),
            schedHaveBlock = block("sched_have"),
            schedNotDoneBlock = block("sched_not_done"),
            schedCompositeBlock = block("sched_composite"),
            schedLeafOrCoroutineBlock = block("sched_leaf_or_coroutine"),
            schedLeafBlock = block("sched_leaf"),
            schedLeafDoneBlock = block("sched_leaf_done"),
            schedLeafParkBlock = block("sched_leaf_park"),
            schedCoroutineBlock = block("sched_coroutine"),
            schedSuspendBlock = block("sched_suspend"),
            schedCompleteBlock = block("sched_complete"),
            schedDeliverBlock = block("sched_deliver"),
            schedAllWaiterBlock = block("sched_all_waiter"),
            schedNotAllWaiterBlock = block("sched_not_all_waiter"),
            schedRaceWaiterBlock = block("sched_race_waiter"),
            schedRaceFirstBlock = block("sched_race_first"),
            schedEnqueueWaiterBlock = block("sched_enqueue_waiter"),
            schedNormalWaiterBlock = block("sched_normal_waiter"),
            schedReturnBlock = block("sched_return"),
            schedDrainBlock = block("sched_drain"),
            schedFinishBlock = block("sched_finish")
        ))

// Pops the next ready task; with the queue empty, the run ends once its main task completed and
// otherwise waits for a parked leaf. A popped task is completed, a composite, a leaf or a coroutine.
let emitSchedulerDispatch (sc: SchedulerContext) (fns: SchedulerFunctions) (blocks: SchedulerBlocks) runFn mainTask taskSlot =
    (let _ = schedAt(sc)(blocks.schedLoopBlock)
    in
        let popped = buildCall(sc.schedBuilder)(fns.dequeueType)(fns.dequeueFn)([])(0u32)("sched_popped")
        in
            let _ = schedSet(sc)(taskSlot)(popped)
            in
                let _ =
                    schedIf(sc)(schedEq(sc)(popped)(schedConst(sc)(0))("sched_queue_empty"))(blocks.schedEmptyBlock)(blocks.schedHaveBlock)
                in
                    let _ = schedAt(sc)(blocks.schedEmptyBlock)
                    in
                        let _ =
                            schedIf(sc)(schedEq(sc)(schedWord(sc)(mainTask)(offsetStateIndex)("sched_main_state"))(stateConst(sc.schedI64)(CompletedTaskState))("sched_main_done"))(blocks.schedReturnBlock)(blocks.schedWaitBlock)
                        in
                            let _ = schedAt(sc)(blocks.schedWaitBlock)
                            in
                                let _ = buildCall(sc.schedBuilder)(fns.waitParkedType)(fns.waitParkedFn)([])(0u32)("")
                                in
                                    let _ = schedBr(sc)(blocks.schedLoopBlock)
                                    in
                                        let _ = schedAt(sc)(blocks.schedHaveBlock)
                                        in
                                            let task = schedLoad(sc)(taskSlot)("sched_task")
                                            in
                                                let stateWord = schedWord(sc)(task)(offsetStateIndex)("sched_state")
                                                in
                                                    let _ =
                                                        schedIf(sc)(schedEq(sc)(stateWord)(stateConst(sc.schedI64)(CompletedTaskState))("sched_is_done"))(blocks.schedCompleteBlock)(blocks.schedNotDoneBlock)
                                                    in
                                                        let _ = schedAt(sc)(blocks.schedNotDoneBlock)
                                                        in
                                                            let isComposite =
                                                                buildOr(sc.schedBuilder)(buildOr(sc.schedBuilder)(schedEq(sc)(stateWord)(stateConst(sc.schedI64)(AllCompositeTaskState))("sched_is_all"))(schedEq(sc)(stateWord)(stateConst(sc.schedI64)(RaceCompositeTaskState))("sched_is_race"))("sched_is_list_composite"))(schedEq(sc)(stateWord)(stateConst(sc.schedI64)(ScopeCompositeTaskState))("sched_is_scope"))("sched_is_composite")
                                                            in
                                                                let _ = schedIf(sc)(isComposite)(blocks.schedCompositeBlock)(blocks.schedLeafOrCoroutineBlock)
                                                                in
                                                                    let _ = schedAt(sc)(blocks.schedCompositeBlock)
                                                                    in
                                                                        let compositeStatus =
                                                                            schedWithTaskArena(sc)(runFn)(task)("sched_composite")(given (_unit) -> buildCall(sc.schedBuilder)(fns.stepCompositeType)(fns.stepCompositeFn)([task])(1u32)("sched_composite_status"))
                                                                        in
                                                                            let _ =
                                                                                schedIf(sc)(schedEq(sc)(compositeStatus)(schedConst(sc)(0))("sched_composite_pending"))(blocks.schedLoopBlock)(blocks.schedCompleteBlock)
                                                                            in
                                                                                let _ = schedAt(sc)(blocks.schedLeafOrCoroutineBlock)
                                                                                in
                                                                                    let _ =
                                                                                        schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateSlt)(stateWord)(stateConst(sc.schedI64)(CompletedTaskState))("sched_is_leaf"))(blocks.schedLeafBlock)(blocks.schedCoroutineBlock)
                                                                                    in task)

// "Ashes: socket and TLS tasks are not supported by the self-hosted compiler yet\n": a leaf task
// other than a sleep or a socket or HTTP leaf (a TLS leaf) stops the program; TLS is not stepped here.
let unportedLeafMessageCodes = [65, 115, 104, 101, 115, 58, 32, 115, 111, 99, 107, 101, 116, 32, 97, 110, 100, 32, 84, 76, 83, 32, 116, 97, 115, 107, 115, 32, 97, 114, 101, 32, 110, 111, 116, 32, 115, 117, 112, 112, 111, 114, 116, 101, 100, 32, 98, 121, 32, 116, 104, 101, 32, 115, 101, 108, 102, 45, 104, 111, 115, 116, 101, 100, 32, 99, 111, 109, 112, 105, 108, 101, 114, 32, 121, 101, 116, 10]

// Steps a socket leaf through the step function its state code selects: a step that completes
// goes on to the completion, one that answers 0 parks the task.
let recursive emitSocketLeafDispatch (sc: SchedulerContext) (blocks: SchedulerBlocks) runFn task stateWord parkBlock unportedBlock (steps: List((Int, LLVMValueRef, LLVMTypeRef))) =
    match steps with
        | [] -> schedBr(sc)(unportedBlock)
        | (code, stepFn, stepType) :: rest ->
            let stepBlock = appendBasicBlock(sc.schedContext)(runFn)("sched_socket_leaf_step")
            in
                let nextBlock = appendBasicBlock(sc.schedContext)(runFn)("sched_socket_leaf_next")
                in
                    let _ =
                        schedIf(sc)(schedEq(sc)(stateWord)(wordConst(sc.schedI64)(code))("sched_socket_leaf_kind"))(stepBlock)(nextBlock)
                    in
                        let _ = schedAt(sc)(stepBlock)
                        in
                            let status =
                                schedWithTaskArena(sc)(runFn)(task)("sched_socket_leaf")(given (_unit) -> buildCall(sc.schedBuilder)(stepType)(stepFn)([task])(1u32)("sched_socket_leaf_status"))
                            in
                                let _ =
                                    schedIf(sc)(schedEq(sc)(status)(schedConst(sc)(0))("sched_socket_leaf_pending"))(parkBlock)(blocks.schedCompleteBlock)
                                in
                                    let _ = schedAt(sc)(nextBlock)
                                    in emitSocketLeafDispatch(sc)(blocks)(runFn)(task)(stateWord)(parkBlock)(unportedBlock)(rest)

// A leaf other than a sleep: a socket or HTTP leaf is stepped, completing or parking on its socket
// (the aggregate wait has no socket readiness yet, so a parked socket leaf is stepped again once the
// queue drains); any other leaf (TLS) stops the program.
let emitOtherLeaf (sc: SchedulerContext) (fns: SchedulerFunctions) (blocks: SchedulerBlocks) runFn task =
    (let parkBlock = appendBasicBlock(sc.schedContext)(runFn)("sched_socket_park")
    in
        let unportedBlock = appendBasicBlock(sc.schedContext)(runFn)("sched_unported_kind")
        in
            let stateWord = schedWord(sc)(task)(offsetStateIndex)("sched_other_leaf_state")
            in
                let _ = emitSocketLeafDispatch(sc)(blocks)(runFn)(task)(stateWord)(parkBlock)(unportedBlock)(fns.socketLeafSteps)
                in
                    let _ = schedAt(sc)(unportedBlock)
                    in
                        let _ = emitProcessPanicMessage(sc.schedBuilder)(sc.schedI64)(sc.schedI8)(unportedLeafMessageCodes)("sched_unported")
                        in
                            let _ = schedAt(sc)(parkBlock)
                            in
                                let _ =
                                    "sched_socket_parked_head"
                                    |> schedLoad(sc)(parkedHeadGlobal(sc))
                                    |> schedStore(sc)(task)(offsetReadyNext)
                                in
                                    let _ =
                                        schedSet(sc)(parkedHeadGlobal(sc))(task)
                                    in schedBr(sc)(blocks.schedLoopBlock))

// A sleeping leaf completes with `Ok(0)` once its remaining time is spent and parks on its timer
// until then; a coroutine is stepped with its frame and, when it suspends, the task it awaits is
// scheduled with it as the waiter.
let emitSchedulerStep (sc: SchedulerContext) (fns: SchedulerFunctions) (blocks: SchedulerBlocks) runFn closureFnType task =
    (let _ = schedAt(sc)(blocks.schedLeafBlock)
    in
        let sleepBlock = appendBasicBlock(sc.schedContext)(runFn)("sched_sleep_leaf")
        in
            let unportedBlock = appendBasicBlock(sc.schedContext)(runFn)("sched_unported_leaf")
            in
                let _ =
                    schedIf(sc)(schedEq(sc)(schedWord(sc)(task)(offsetStateIndex)("sched_leaf_state"))(stateConst(sc.schedI64)(SleepingTaskState))("sched_is_sleep"))(sleepBlock)(unportedBlock)
                in
                    let _ = schedAt(sc)(unportedBlock)
                    in
                        let _ = emitOtherLeaf(sc)(fns)(blocks)(runFn)(task)
                        in
                            let _ = schedAt(sc)(sleepBlock)
                            in
                                let _ =
                                    schedIf(sc)(buildICmp(sc.schedBuilder)(intPredicateSle)(schedWord(sc)(task)(offsetSleepDurationMilliseconds)("sched_sleep_left"))(schedConst(sc)(0))("sched_sleep_elapsed"))(blocks.schedLeafDoneBlock)(blocks.schedLeafParkBlock)
                                in
                                    let _ = schedAt(sc)(blocks.schedLeafDoneBlock)
                                    in
                                        let _ =
                                            (given (_unit) ->
                                                schedOk(sc)(runFn)(schedConst(sc)(0))("sched_sleep_result"))
                                            |> schedWithTaskArena(sc)(runFn)(task)("sched_sleep")
                                            |> schedStore(sc)(task)(offsetResultSlot)
                                        in
                                            let _ =
                                                CompletedTaskState
                                                |> stateConst(sc.schedI64)
                                                |> schedStore(sc)(task)(offsetStateIndex)
                                            in
                                                let _ =
                                                    NoTaskWait
                                                    |> taskWaitCode
                                                    |> wordConst(sc.schedI64)
                                                    |> schedStore(sc)(task)(offsetWaitKind)
                                                in
                                                    let _ = schedBr(sc)(blocks.schedCompleteBlock)
                                                    in
                                                        let _ = schedAt(sc)(blocks.schedLeafParkBlock)
                                                        in
                                                            let _ =
                                                                TimerTaskWait
                                                                |> taskWaitCode
                                                                |> wordConst(sc.schedI64)
                                                                |> schedStore(sc)(task)(offsetWaitKind)
                                                            in
                                                                let _ =
                                                                    "sched_parked_head"
                                                                    |> schedLoad(sc)(parkedHeadGlobal(sc))
                                                                    |> schedStore(sc)(task)(offsetReadyNext)
                                                                in
                                                                    let _ =
                                                                        schedSet(sc)(parkedHeadGlobal(sc))(task)
                                                                    in
                                                                        let _ = schedBr(sc)(blocks.schedLoopBlock)
                                                                        in
                                                                            let _ = schedAt(sc)(blocks.schedCoroutineBlock)
                                                                            in
                                                                                let codeWord = schedWord(sc)(task)(offsetCoroutineFunction)("sched_coroutine_code")
                                                                                in
                                                                                    let status =
                                                                                        schedWithTaskArena(sc)(runFn)(task)("sched_coroutine")(given (_unit) ->
                                                                                            buildCall(sc.schedBuilder)(closureFnType)(buildIntToPtr(sc.schedBuilder)(codeWord)(sc.schedPtr)("sched_coroutine_fn"))([task, schedConst(sc)(0), schedConst(sc)(0)])(3u32)("sched_coroutine_status"))
                                                                                    in
                                                                                        let _ =
                                                                                            schedIf(sc)(schedEq(sc)(status)(schedConst(sc)(0))("sched_suspended"))(blocks.schedSuspendBlock)(blocks.schedCompleteBlock)
                                                                                        in
                                                                                            let _ = schedAt(sc)(blocks.schedSuspendBlock)
                                                                                            in
                                                                                                let awaited = schedWord(sc)(task)(offsetAwaitedTask)("sched_awaited")
                                                                                                in
                                                                                                    let _ = schedStore(sc)(awaited)(offsetWaiter)(task)
                                                                                                    in
                                                                                                        let _ =
                                                                                                            "sched_owner"
                                                                                                            |> schedWord(sc)(task)(offsetArenaOwner)
                                                                                                            |> schedStore(sc)(awaited)(offsetArenaOwner)
                                                                                                        in
                                                                                                            let _ = schedEnqueue(sc)(awaited)
                                                                                                            in schedBr(sc)(blocks.schedLoopBlock))

// A completed task hands its result to its waiter: an `all` composite counts it and is scheduled
// when the last child is in, an unresolved `race` takes it as its result, cancels the other
// children and is scheduled, any other waiter takes it into its result slot and is scheduled.
let emitSchedulerComplete (sc: SchedulerContext) (fns: SchedulerFunctions) (blocks: SchedulerBlocks) runFn taskSlot raceCursorSlot =
    (let _ = schedAt(sc)(blocks.schedCompleteBlock)
    in
        let completed = schedLoad(sc)(taskSlot)("sched_completed")
        in
            let waiter = schedWord(sc)(completed)(offsetWaiter)("sched_waiter")
            in
                let _ =
                    schedIf(sc)(schedEq(sc)(waiter)(schedConst(sc)(0))("sched_no_waiter"))(blocks.schedLoopBlock)(blocks.schedDeliverBlock)
                in
                    let _ = schedAt(sc)(blocks.schedDeliverBlock)
                    in
                        let waiterState = schedWord(sc)(waiter)(offsetStateIndex)("sched_waiter_state")
                        in
                            let _ =
                                schedIf(sc)(schedEq(sc)(waiterState)(stateConst(sc.schedI64)(AllCompositeTaskState))("sched_waiter_is_all"))(blocks.schedAllWaiterBlock)(blocks.schedNotAllWaiterBlock)
                            in
                                let _ = schedAt(sc)(blocks.schedAllWaiterBlock)
                                in
                                    let remaining =
                                        buildSub(sc.schedBuilder)(schedWord(sc)(waiter)(offsetWaitData0)("sched_all_counter"))(schedConst(sc)(1))("sched_all_remaining")
                                    in
                                        let _ = schedStore(sc)(waiter)(offsetWaitData0)(remaining)
                                        in
                                            let _ =
                                                schedIf(sc)(schedEq(sc)(remaining)(schedConst(sc)(0))("sched_all_ready"))(blocks.schedEnqueueWaiterBlock)(blocks.schedLoopBlock)
                                            in
                                                let _ = schedAt(sc)(blocks.schedNotAllWaiterBlock)
                                                in
                                                    let _ =
                                                        schedIf(sc)(schedEq(sc)(waiterState)(stateConst(sc.schedI64)(RaceCompositeTaskState))("sched_waiter_is_race"))(blocks.schedRaceWaiterBlock)(blocks.schedNormalWaiterBlock)
                                                    in
                                                        let _ = schedAt(sc)(blocks.schedRaceWaiterBlock)
                                                        in
                                                            let _ =
                                                                schedIf(sc)(schedEq(sc)(schedWord(sc)(waiter)(offsetWaitData0)("sched_race_resolved"))(schedConst(sc)(0))("sched_race_open"))(blocks.schedRaceFirstBlock)(blocks.schedLoopBlock)
                                                            in
                                                                let _ = schedAt(sc)(blocks.schedRaceFirstBlock)
                                                                in
                                                                    let _ =
                                                                        "sched_race_result"
                                                                        |> schedWord(sc)(completed)(offsetResultSlot)
                                                                        |> schedStore(sc)(waiter)(offsetResultSlot)
                                                                    in
                                                                        let _ =
                                                                            1
                                                                            |> schedConst(sc)
                                                                            |> schedStore(sc)(waiter)(offsetWaitData0)
                                                                        in
                                                                            let _ = emitCancelRaceLosers(sc)(fns)(runFn)(raceCursorSlot)(waiter)(completed)
                                                                            in
                                                                                let _ = schedBr(sc)(blocks.schedEnqueueWaiterBlock)
                                                                                in
                                                                                    let _ = schedAt(sc)(blocks.schedEnqueueWaiterBlock)
                                                                                    in
                                                                                        let _ = schedEnqueue(sc)(waiter)
                                                                                        in
                                                                                            let _ = schedBr(sc)(blocks.schedLoopBlock)
                                                                                            in
                                                                                                let _ = schedAt(sc)(blocks.schedNormalWaiterBlock)
                                                                                                in
                                                                                                    let _ =
                                                                                                        "sched_delivered"
                                                                                                        |> schedWord(sc)(completed)(offsetResultSlot)
                                                                                                        |> schedStore(sc)(waiter)(offsetResultSlot)
                                                                                                    in
                                                                                                        let _ =
                                                                                                            0
                                                                                                            |> schedConst(sc)
                                                                                                            |> schedStore(sc)(waiter)(offsetAwaitedTask)
                                                                                                        in
                                                                                                            let _ = schedEnqueue(sc)(waiter)
                                                                                                            in schedBr(sc)(blocks.schedLoopBlock))

// Stage 0's `ashes_scheduler_run`: the run queue seeded with the main task and stepped until the
// main task completes. Only the outermost run drops what is still queued or parked when it ends;
// a nested run shares the queues with the run around it.
let defineSchedulerRun module_ (sc: SchedulerContext) (fns: SchedulerFunctions) closureFnType =
    (let runType = functionType(sc.schedI64)([sc.schedI64])(1u32)(false)
    in
        let runFn = addInternalFunction(module_)("__ashes_scheduler_run")(runType)
        in
            let entryBlock = appendBasicBlock(sc.schedContext)(runFn)("entry")
            in
                let blocks = schedulerBlocks(sc.schedContext)(runFn)
                in
                    let mainTask = getParam(runFn)(0u32)
                    in
                        let _ = schedAt(sc)(entryBlock)
                        in
                            let taskSlot = schedSlot(sc)("sched_task_slot")
                            in
                                let raceCursorSlot = schedSlot(sc)("sched_race_cursor")
                                in
                                    let _ =
                                        0
                                        |> schedConst(sc)
                                        |> schedStore(sc)(mainTask)(offsetWaiter)
                                    in
                                        let _ =
                                            0
                                            |> schedConst(sc)
                                            |> schedStore(sc)(mainTask)(offsetArenaOwner)
                                        in
                                            let _ =
                                                "sched_depth_inc"
                                                |> buildAdd(sc.schedBuilder)(schedLoad(sc)(depthGlobal(sc))("sched_depth_in"))(schedConst(sc)(1))
                                                |> schedSet(sc)(depthGlobal(sc))
                                            in
                                                let _ = schedEnqueue(sc)(mainTask)
                                                in
                                                    let _ = schedBr(sc)(blocks.schedLoopBlock)
                                                    in
                                                        let task = emitSchedulerDispatch(sc)(fns)(blocks)(runFn)(mainTask)(taskSlot)
                                                        in
                                                            let _ = emitSchedulerStep(sc)(fns)(blocks)(runFn)(closureFnType)(task)
                                                            in
                                                                let _ = emitSchedulerComplete(sc)(fns)(blocks)(runFn)(taskSlot)(raceCursorSlot)
                                                                in
                                                                    let _ = schedAt(sc)(blocks.schedReturnBlock)
                                                                    in
                                                                        let remainingDepth =
                                                                            buildSub(sc.schedBuilder)(schedLoad(sc)(depthGlobal(sc))("sched_depth_out"))(schedConst(sc)(1))("sched_depth_dec")
                                                                        in
                                                                            let _ =
                                                                                schedSet(sc)(depthGlobal(sc))(remainingDepth)
                                                                            in
                                                                                let _ =
                                                                                    schedIf(sc)(schedEq(sc)(remainingDepth)(schedConst(sc)(0))("sched_is_outermost"))(blocks.schedDrainBlock)(blocks.schedFinishBlock)
                                                                                in
                                                                                    let _ = schedAt(sc)(blocks.schedDrainBlock)
                                                                                    in
                                                                                        let _ =
                                                                                            0
                                                                                            |> schedConst(sc)
                                                                                            |> schedSet(sc)(readyHeadGlobal(sc))
                                                                                        in
                                                                                            let _ =
                                                                                                0
                                                                                                |> schedConst(sc)
                                                                                                |> schedSet(sc)(readyTailGlobal(sc))
                                                                                            in
                                                                                                let _ =
                                                                                                    0
                                                                                                    |> schedConst(sc)
                                                                                                    |> schedSet(sc)(parkedHeadGlobal(sc))
                                                                                                in
                                                                                                    let _ = schedBr(sc)(blocks.schedFinishBlock)
                                                                                                    in
                                                                                                        let _ = schedAt(sc)(blocks.schedFinishBlock)
                                                                                                        in
                                                                                                            let _ =
                                                                                                                "sched_result"
                                                                                                                |> schedWord(sc)(mainTask)(offsetResultSlot)
                                                                                                                |> buildRet(sc.schedBuilder)
                                                                                                            in TaskRuntime(runTaskFn = runFn, runTaskType = runType, spawnEnqueueFn = sc.enqueueFn, spawnEnqueueType = sc.enqueueType, forkScopedFn = fns.forkScopedFn, forkScopedType = fns.forkScopedType))

// The scheduler and its helpers, defined once per module that has a coroutine.
let defineTaskRuntime module_ context builder i64 i8 ptrType (arena: ArenaRuntime) closureFnType =
    (let globals =
        SchedulerGlobals(
            readyQueueHead = addZeroWordGlobal(module_)(i64)("__ashes_ready_head"),
            readyQueueTail = addZeroWordGlobal(module_)(i64)("__ashes_ready_tail"),
            parkedLeafHead = addZeroWordGlobal(module_)(i64)("__ashes_parked_head"),
            schedulerRunDepth = addZeroWordGlobal(module_)(i64)("__ashes_scheduler_depth"),
            epollDescriptor = addZeroWordGlobal(module_)(i64)("__ashes_epoll_fd")
        )
    in
        match defineReadyEnqueue(module_)(context)(builder)(i64)(i8)(ptrType)(globals) with
            | (enqueueFn, enqueueType) ->
                let sc =
                    SchedulerContext(
                        schedContext = context,
                        schedBuilder = builder,
                        schedI64 = i64,
                        schedI8 = i8,
                        schedPtr = ptrType,
                        schedArena = arena,
                        schedGlobals = globals,
                        enqueueFn = enqueueFn,
                        enqueueType = enqueueType
                    )
                in
                    match defineCancelTask(module_)(sc) with
                        | (cancelFn, cancelType) ->
                            match defineStepScope(module_)(sc)(cancelFn)(cancelType) with
                                | (scopeFn, scopeType) ->
                                    match (defineReadyDequeue(module_)(context)(builder)(i64)(i8)(ptrType)(globals), defineStepComposite(module_)(sc)(scopeFn)(scopeType), defineWaitParked(module_)(sc), defineForkScoped(module_)(sc), defineStepTcpConnect(module_)(sc)) with
                                        | ((dequeueFn, dequeueType), (stepCompositeFn, stepCompositeType), (waitParkedFn, waitParkedType), (forkScopedFn, forkScopedType), (stepTcpConnectFn, stepTcpConnectType)) ->
                                            match defineStepHttp(module_)(sc)(stepTcpConnectFn)(stepTcpConnectType) with
                                                | (stepHttpFn, stepHttpType) ->
                                                    let leafStep =
                                                        given (kind: TaskStateKind) ->
                                                            given (step: (LLVMValueRef, LLVMTypeRef)) ->
                                                                match step with
                                                                    | (stepFn, stepType) -> (taskStateCode(kind), stepFn, stepType)
                                                    in
                                                        let socketLeafSteps =
                                                            [
                                                                leafStep(TcpConnectTaskState)((stepTcpConnectFn, stepTcpConnectType)),
                                                                leafStep(HttpGetTaskState)((stepHttpFn, stepHttpType)),
                                                                leafStep(HttpPostTaskState)((stepHttpFn, stepHttpType)),
                                                                sc
                                                                |> emitStepTcpSend
                                                                |> defineLeafStep(module_)(sc)("__ashes_step_tcp_send")
                                                                |> leafStep(TcpSendTaskState),
                                                                sc
                                                                |> emitStepTcpReceive
                                                                |> defineLeafStep(module_)(sc)("__ashes_step_tcp_receive")
                                                                |> leafStep(TcpReceiveTaskState),
                                                                sc
                                                                |> emitStepTcpClose
                                                                |> defineLeafStep(module_)(sc)("__ashes_step_tcp_close")
                                                                |> leafStep(TcpCloseTaskState),
                                                                sc
                                                                |> emitStepTcpListen
                                                                |> defineLeafStep(module_)(sc)("__ashes_step_tcp_listen")
                                                                |> leafStep(TcpListenTaskState),
                                                                sc
                                                                |> emitStepTcpAccept
                                                                |> defineLeafStep(module_)(sc)("__ashes_step_tcp_accept")
                                                                |> leafStep(TcpAcceptTaskState)
                                                            ]
                                                        in
                                                            closureFnType |> defineSchedulerRun(module_)(sc)(SchedulerFunctions(
                                                                stepCompositeFn = stepCompositeFn,
                                                                stepCompositeType = stepCompositeType,
                                                                waitParkedFn = waitParkedFn,
                                                                waitParkedType = waitParkedType,
                                                                cancelFn = cancelFn,
                                                                cancelType = cancelType,
                                                                dequeueFn = dequeueFn,
                                                                dequeueType = dequeueType,
                                                                forkScopedFn = forkScopedFn,
                                                                forkScopedType = forkScopedType,
                                                                socketLeafSteps = socketLeafSteps
                                                            )))

// `Ashes.Task.spawn(task)`, stage 0's `EmitSpawnTask` on the run queue: the task's frame is copied
// to the start of a fresh 4 MiB chunk that becomes its private arena, the copy is its own arena
// owner with no waiter, and it is scheduled. Its arena is not reclaimed when it completes.
let emitSpawnTask context function_ builder i64 i8 ptrType (runtime: TaskRuntime) memcpyFn memcpyType taskRef name =
    (let chunkBase =
        arenaChunkBytes
        |> wordConst(i64)
        |> emitLinuxMmapAnonymous(builder)(i64)
    in
        let chunkEnd =
            buildAdd(builder)(chunkBase)(wordConst(i64)(arenaChunkBytes - 8))(name + "_chunk_end")
        in
            let _ =
                0
                |> wordConst(i64)
                |> storeTaskWord(builder)(i64)(i8)(ptrType)(chunkBase)(0)
            in
                let _ = storeTaskWord(builder)(i64)(i8)(ptrType)(chunkEnd)(0)(chunkBase)
                in
                    let copyRef =
                        buildAdd(builder)(chunkBase)(wordConst(i64)(8))(name + "_copy")
                    in
                        let frameSize = loadTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(offsetFrameSizeBytes)(name + "_frame_size")
                        in
                            let _ = buildCall(builder)(memcpyType)(memcpyFn)([buildIntToPtr(builder)(copyRef)(ptrType)(name + "_dest"), buildIntToPtr(builder)(taskRef)(ptrType)(name + "_source"), frameSize])(3u32)(name + "_frame_copy")
                            in
                                let alignedSize =
                                    buildAnd(builder)(buildAdd(builder)(frameSize)(wordConst(i64)(7))(name + "_size_plus7"))(wordConst(i64)(-8))(name + "_size_aligned")
                                in
                                    let _ =
                                        name + "_arena_cursor"
                                        |> buildAdd(builder)(copyRef)(alignedSize)
                                        |> storeTaskWord(builder)(i64)(i8)(ptrType)(copyRef)(offsetArenaCursor)
                                    in
                                        let _ = storeTaskWord(builder)(i64)(i8)(ptrType)(copyRef)(offsetArenaEnd)(chunkEnd)
                                        in
                                            let _ = storeTaskWord(builder)(i64)(i8)(ptrType)(copyRef)(offsetArenaOwner)(copyRef)
                                            in
                                                let _ =
                                                    0
                                                    |> wordConst(i64)
                                                    |> storeTaskWord(builder)(i64)(i8)(ptrType)(copyRef)(offsetWaiter)
                                                in buildCall(builder)(runtime.spawnEnqueueType)(runtime.spawnEnqueueFn)([copyRef])(1u32)(name + "_enqueue"))

let emitRunTask builder (runtime: TaskRuntime) taskRef name = buildCall(builder)(runtime.runTaskType)(runtime.runTaskFn)([taskRef])(1u32)(name)

// A task scope, stage 0's `EmitCreateTaskScope`: `[head][tail][owning composite][closed][explicit]`,
// the fork-node list of its children and whether `Ashes.Task.scope` introduced it.
let emitCreateTaskScope context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (explicit: Bool) name =
    (let scope = emitArenaAlloc(context)(function_)(builder)(i64)(arena)(40)(name)
    in
        let _ = storeZeroTaskWords(builder)(i64)(i8)(ptrType)(scope)([0, 8, 16, 24])
        in
            let _ =
                (if explicit
                then 1
                else 0)
                |> wordConst(i64)
                |> storeTaskWord(builder)(i64)(i8)(ptrType)(scope)(32)
            in scope)

// A scope composite over the scoped task, stage 0's `EmitCreateScopedTask`.
let emitCreateScopedTask context function_ builder i64 i8 ptrType (arena: ArenaRuntime) parentRef scopeRef name =
    (let task =
        emitHeaderTask(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(taskStateCode(ScopeCompositeTaskState))([(offsetIoArgument0, parentRef), (offsetAwaitedTask, parentRef), (offsetIoArgument1, scopeRef)])(name)
    in
        let _ = storeTaskWord(builder)(i64)(i8)(ptrType)(scopeRef)(16)(task)
        in task)

let emitForkScopedTask builder (runtime: TaskRuntime) ownerRef childRef name = buildCall(builder)(runtime.forkScopedType)(runtime.forkScopedFn)([ownerRef, childRef])(2u32)(name)

// `Ashes.Task.join(handle)`, stage 0's `EmitJoinScopedTask`: the fork node is marked joined and its
// child task is the task the join awaits.
let emitJoinScopedTask builder i64 i8 ptrType handleRef name =
    (let _ =
        1
        |> wordConst(i64)
        |> storeTaskWord(builder)(i64)(i8)(ptrType)(handleRef)(8)
    in loadTaskWord(builder)(i64)(i8)(ptrType)(handleRef)(0)(name))
