// Structured parallelism for `Ashes.Task.Parallel`, stage 0's `LlvmCodegenParallel.cs` for
// linux-x64. `ParallelFork` runs the right thunk of `both` on a worker thread while the caller runs
// the left one: the worker is a raw `clone` thread on its own mapped stack whose GS base is its own
// control block, so it bumps its own arena (`IrCodegen.Arena`'s thread slots) and its own stretch
// of the reference-counted range (`IrCodegen.RcRegion`). When the worker cap (the detected core
// count, narrowed by `withWorkers`) is reached, the thunk runs inline instead. `ParallelJoin` waits
// on the descriptor's done word with a futex and answers the worker's result, which still lives in
// the worker's arena; `ParallelCleanup` waits for the thread to exit and unmaps its stack, arena
// chunks and control block. `ParallelQueueStart` runs stage 0's work-conserving reduce queue:
// workers claim list elements from a shared counter, publish their mapped values, then merge
// adjacent results round by round; `ParallelQueueAwait` waits for the merge root and
// `ParallelQueueCleanup` reclaims the workers and the queue region.
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Syscalls.LinuxX64
import AshesCompiler.Backend.IrCodegen.Arena
import Ashes.Number.UInt
export (
    type ParallelRuntime,
    value defineParallelRuntime,
    value emitParallelFork,
    value emitParallelJoin,
    value emitParallelCleanup,
    value emitLoadParallelWorkerOverride,
    value emitStoreParallelWorkerOverride,
    value emitParallelQueueStart,
    value emitParallelQueueAwait,
    value emitParallelQueueCleanup,
)

// The fork descriptor, stage 0's layout: the done word the worker futex-wakes, the result, whether
// the thunk ran on a worker, the thunk, the worker's stack and control block, and at 56 the word
// the kernel clears and wakes when the thread has exited (`CLONE_CHILD_CLEARTID`).
let descDone = 0

let descResult = 8

let descMode = 16

let descRightClosure = 24

let descWorkerStack = 32

let descWorkerBlock = 40

let descExited = 56

let descBytes = 72

// Stage 0's default worker stack (1 MiB) and the cap used when core-count detection fails.
let workerStackBytes = 1048576

let workerCapFallback = 8

// `CLONE_VM | CLONE_FS | CLONE_FILES | CLONE_SIGHAND | CLONE_THREAD | CLONE_SYSVSEM |
// CLONE_CHILD_CLEARTID`.
let cloneFlags = 2428672

// `FUTEX_WAIT_PRIVATE`, `FUTEX_WAKE_PRIVATE`, and the shared `FUTEX_WAIT` the exit word needs.
let futexWaitPrivate = 128

let futexWakePrivate = 129

let futexWait = 0

let syscallFutex = 202

let syscallSchedGetaffinity = 204

let syscallArchPrctl = 158

let archSetGs = 4097

// The arena chunk layout `IrCodegen.Arena` uses: an 8-byte header holding the previous chunk's end
// and an 8-byte footer at the end holding the chunk's base.
let chunkFooterBytes = 8

// The globals and functions of a program that uses `Ashes.Task.Parallel`: the `withWorkers`
// override, and with worker threads the running-worker counter, the cached cap and its function,
// the function each worker thread runs, and the work-conserving queue's start, await and cleanup
// functions.
type ParallelRuntime =
    | overrideGlobal: LLVMValueRef
    | activeCounter: Maybe(LLVMValueRef)
    | capFn: LLVMValueRef
    | capType: LLVMTypeRef
    | workerFn: LLVMValueRef
    | queueStartFn: LLVMValueRef
    | queueStartType: LLVMTypeRef
    | queueAwaitFn: LLVMValueRef
    | queueAwaitType: LLVMTypeRef
    | queueCleanupFn: LLVMValueRef
    | queueCleanupType: LLVMTypeRef

let parConst i64 (value: Int) =
    constInt(i64)(Ashes.Number.UInt.fromInt64(value))(true)

let parZeroGlobal module_ i64 name =
    (let global = addGlobal(module_)(i64)(name)
    in
        let _ =
            0
            |> parConst(i64)
            |> setInitializer(global)
        in
            let _ = setLinkage(global)(linkageInternal)
            in global)

let parWord builder i64 i8 ptrType address (offset: Int) name =
    buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(address)(offset)(name + "_ptr"))(name)

let parStore builder i64 i8 ptrType address (offset: Int) value name =
    name + "_ptr"
    |> memOffsetPtr(builder)(i64)(i8)(ptrType)(address)(offset)
    |> buildStore(builder)(value)

// `futex(address, operation, value, 0, 0, 0)`.
let emitFutex builder i64 address (operation: Int) (value: Int) name =
    emitLinuxSyscallCall6(builder)(i64)(parConst(i64)(syscallFutex))(address)(parConst(i64)(operation))(parConst(i64)(value))(parConst(i64)(0))(parConst(i64)(0))(parConst(i64)(0))(name)

let recursive sumPopulations builder i64 i8 ptrType ctpopFn ctpopType mask (word: Int) total =
    if word >= 16
    then total
    else
        let bits = parWord(builder)(i64)(i8)(ptrType)(mask)(word * 8)("cap_mask_word")
        in
            let count = buildCall(builder)(ctpopType)(ctpopFn)([bits])(1u32)("cap_word_count")
            in
                "cap_total"
                |> buildAdd(builder)(total)(count)
                |> sumPopulations(builder)(i64)(i8)(ptrType)(ctpopFn)(ctpopType)(mask)(word + 1)

let recursive zeroMaskWords builder i64 i8 ptrType mask (word: Int) =
    if word >= 16
    then Unit
    else
        let _ =
            parStore(builder)(i64)(i8)(ptrType)(mask)(word * 8)(parConst(i64)(0))("cap_mask_zero")
        in zeroMaskWords(builder)(i64)(i8)(ptrType)(mask)(word + 1)

// Stage 0's `__ashes_parallel_cap_get`: the number of CPUs this thread may run on, counted once
// from `sched_getaffinity`'s 1024-CPU mask and cached; 8 when the count comes back empty.
let defineWorkerCapFunction module_ context builder i64 i8 ptrType =
    (let capType = functionType(i64)([])(0u32)(false)
    in
        let capFn = addInternalFunction(module_)("__ashes_parallel_cap_get")(capType)
        in
            let cached = parZeroGlobal(module_)(i64)("__ashes_parallel_cap")
            in
                let ctpopType = functionType(i64)([i64])(1u32)(false)
                in
                    let ctpopFn = addFunction(module_)("llvm.ctpop.i64")(ctpopType)
                    in
                        let entryBlock = appendBasicBlock(context)(capFn)("entry")
                        in
                            let cachedBlock = appendBasicBlock(context)(capFn)("cap_cached")
                            in
                                let detectBlock = appendBasicBlock(context)(capFn)("cap_detect")
                                in
                                    let _ = positionBuilderAtEnd(builder)(entryBlock)
                                    in
                                        let mask =
                                            buildPtrToInt(builder)(buildEntryAlloca(builder)(arrayType(i64)(16u64))("cap_mask"))(i64)("cap_mask_addr")
                                        in
                                            let current = buildLoad(builder)(i64)(cached)("cap_current")
                                            in
                                                let _ =
                                                    buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(current)(parConst(i64)(0))("cap_known"))(cachedBlock)(detectBlock)
                                                in
                                                    let _ = positionBuilderAtEnd(builder)(cachedBlock)
                                                    in
                                                        let _ = buildRet(builder)(current)
                                                        in
                                                            let _ = positionBuilderAtEnd(builder)(detectBlock)
                                                            in
                                                                let _ = zeroMaskWords(builder)(i64)(i8)(ptrType)(mask)(0)
                                                                in
                                                                    let _ =
                                                                        emitLinuxSyscallCall(builder)(i64)(parConst(i64)(syscallSchedGetaffinity))(parConst(i64)(0))(parConst(i64)(128))(mask)("cap_getaffinity")
                                                                    in
                                                                        let detected =
                                                                            0
                                                                            |> parConst(i64)
                                                                            |> sumPopulations(builder)(i64)(i8)(ptrType)(ctpopFn)(ctpopType)(mask)(0)
                                                                        in
                                                                            let resolved =
                                                                                buildSelect(builder)(buildICmp(builder)(intPredicateSle)(detected)(parConst(i64)(0))("cap_none"))(parConst(i64)(workerCapFallback))(detected)("cap_resolved")
                                                                            in
                                                                                let _ = buildStore(builder)(resolved)(cached)
                                                                                in
                                                                                    let _ = buildRet(builder)(resolved)
                                                                                    in (capFn, capType))

// Stage 0's worker trampoline for linux-x64: makes the control block the parent prepared this
// thread's GS base, runs the right thunk on `Unit` in the worker's own arena, publishes the result,
// gives its worker slot back, then sets the done word and wakes the joining parent. It returns to
// the clone wrapper, which ends the thread.
let defineWorkerFunction module_ context builder i64 i8 ptrType (arena: ArenaRuntime) closureFnType activeCounter =
    (let workerType =
        functionType(voidType(context))([i64])(1u32)(false)
    in
        let workerFn = addInternalFunction(module_)("__ashes_parallel_worker")(workerType)
        in
            let _ =
                "entry"
                |> appendBasicBlock(context)(workerFn)
                |> positionBuilderAtEnd(builder)
            in
                let desc = getParam(workerFn)(0u32)
                in
                    let block = parWord(builder)(i64)(i8)(ptrType)(desc)(descWorkerBlock)("worker_block")
                    in
                        let _ =
                            emitLinuxSyscallCall(builder)(i64)(parConst(i64)(syscallArchPrctl))(parConst(i64)(archSetGs))(block)(parConst(i64)(0))("worker_set_gs")
                        in
                            let unitValue = emitArenaAllocAdt(context)(workerFn)(builder)(i64)(i8)(ptrType)(arena)(0)(0)(false)("worker_unit")
                            in
                                let result =
                                    emitCallClosure(builder)(i64)(i8)(ptrType)(closureFnType)(parWord(builder)(i64)(i8)(ptrType)(desc)(descRightClosure)("worker_right"))(unitValue)(parConst(i64)(0))("worker_result")
                                in
                                    let _ = parStore(builder)(i64)(i8)(ptrType)(desc)(descResult)(result)("worker_store_result")
                                    in
                                        let _ =
                                            buildAtomicRmw(builder)(atomicRmwAdd)(activeCounter)(parConst(i64)(-1))(atomicOrderingSequentiallyConsistent)
                                        in
                                            let _ =
                                                parStore(builder)(i64)(i8)(ptrType)(desc)(descDone)(parConst(i64)(1))("worker_done")
                                            in
                                                let _ = emitFutex(builder)(i64)(desc)(futexWakePrivate)(1)("worker_wake")
                                                in
                                                    let _ = buildRetVoid(builder)
                                                    in workerFn)

let emitLoadParallelWorkerOverride builder i64 (runtime: ParallelRuntime) name = buildLoad(builder)(i64)(runtime.overrideGlobal)(name)

let emitStoreParallelWorkerOverride builder (runtime: ParallelRuntime) value = buildStore(builder)(value)(runtime.overrideGlobal)

// Runs the right thunk on `Unit` in the caller's arena and records the result as done, inline.
let emitForkInline context function_ builder i64 i8 ptrType (arena: ArenaRuntime) closureFnType desc rightClosure =
    (let unitValue = emitArenaAllocAdt(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(0)(0)(false)("par_inline_unit")
    in
        let result =
            emitCallClosure(builder)(i64)(i8)(ptrType)(closureFnType)(rightClosure)(unitValue)(parConst(i64)(0))("par_inline_result")
        in
            let _ = parStore(builder)(i64)(i8)(ptrType)(desc)(descResult)(result)("par_inline_store_result")
            in
                let _ =
                    parStore(builder)(i64)(i8)(ptrType)(desc)(descDone)(parConst(i64)(1))("par_inline_done")
                in
                    parStore(builder)(i64)(i8)(ptrType)(desc)(descMode)(parConst(i64)(0))("par_inline_mode"))

// Stage 0's raw `clone` wrapper (`EmitCloneWorkerX64`): the descriptor is pushed on the child's
// stack, the trampoline kept in `r9` across the system call, and the child pops the descriptor,
// calls the trampoline and exits; the exit-cleared word is the descriptor's word at 56.
let emitCloneWorker context builder i64 desc stackTop workerFn =
    (let asmType =
        functionType(voidType(context))([i64, i64, i64, i64])(4u32)(false)
    in
        let asmText = "mov $0, %rsi\nand $$-16, %rsi\nsub $$8, %rsi\nmov $1, (%rsi)\nmov $2, %r9\nmov $3, %rdi\nxor %edx, %edx\nmov $1, %r10\nadd $$56, %r10\nxor %r8d, %r8d\nmov $$56, %eax\nsyscall\ntest %rax, %rax\njnz 1f\nxor %ebp, %ebp\npop %rdi\ncall *%r9\nmov $$60, %eax\nxor %edi, %edi\nsyscall\n1:\n"
        in
            let callee = getInlineAsm(asmType)(asmText)("r,r,r,r,~{rax},~{rcx},~{r11},~{rdi},~{rsi},~{rdx},~{r8},~{r9},~{r10},~{memory},~{cc}")(true)(false)
            in buildCall(builder)(asmType)(callee)([stackTop, desc, buildPtrToInt(builder)(workerFn)(i64)("par_worker_fn"), parConst(i64)(cloneFlags)])(4u32)(""))

// The cap a fork compares the running workers against: the detected core count, or the
// `withWorkers` override when it is set and lower.
let emitWorkerCap builder i64 capFn capType overrideGlobal =
    (let compiledMax = buildCall(builder)(capType)(capFn)([])(0u32)("par_cap_max")
    in
        let overrideValue = buildLoad(builder)(i64)(overrideGlobal)("par_cap_override")
        in
            let useOverride =
                buildAnd(builder)(buildICmp(builder)(intPredicateNe)(overrideValue)(parConst(i64)(0))("par_cap_override_set"))(buildICmp(builder)(intPredicateSlt)(overrideValue)(compiledMax)("par_cap_override_lower"))("par_cap_use_override")
            in buildSelect(builder)(useOverride)(overrideValue)(compiledMax)("par_cap"))

let emitEffectiveWorkerCap builder i64 (runtime: ParallelRuntime) = emitWorkerCap(builder)(i64)(runtime.capFn)(runtime.capType)(runtime.overrideGlobal)

// Stage 0's `EmitParallelFork`: a descriptor in the caller's arena; a worker slot is claimed with
// an atomic add and, under the cap, the worker gets a control block wired to a fresh arena chunk
// and a mapped stack and is cloned; over the cap the slot is given back and the thunk runs inline.
// Answers the descriptor.
let emitParallelFork context function_ builder i64 i8 ptrType (arena: ArenaRuntime) closureFnType (runtime: ParallelRuntime) rightClosure name =
    (let desc = emitArenaAlloc(context)(function_)(builder)(i64)(arena)(descBytes)(name + "_desc")
    in
        let _ = parStore(builder)(i64)(i8)(ptrType)(desc)(descRightClosure)(rightClosure)(name + "_right")
        in
            let _ =
                parStore(builder)(i64)(i8)(ptrType)(desc)(descDone)(parConst(i64)(0))(name + "_done0")
            in
                match runtime.activeCounter with
                    | None ->
                        let _ = emitForkInline(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(closureFnType)(desc)(rightClosure)
                        in desc
                    | Some(activeCounter) ->
                        let spawnBlock = appendBasicBlock(context)(function_)(name + "_spawn")
                        in
                            let inlineBlock = appendBasicBlock(context)(function_)(name + "_inline")
                            in
                                let mergeBlock = appendBasicBlock(context)(function_)(name + "_forked")
                                in
                                    let previous =
                                        buildAtomicRmw(builder)(atomicRmwAdd)(activeCounter)(parConst(i64)(1))(atomicOrderingSequentiallyConsistent)
                                    in
                                        let canSpawn =
                                            buildICmp(builder)(intPredicateSlt)(previous)(emitEffectiveWorkerCap(builder)(i64)(runtime))(name + "_can_spawn")
                                        in
                                            let _ = buildCondBr(builder)(canSpawn)(spawnBlock)(inlineBlock)
                                            in
                                                let _ = positionBuilderAtEnd(builder)(spawnBlock)
                                                in
                                                    let block =
                                                        emitArenaOsAllocate(context)(function_)(builder)(i64)(arena)(parConst(i64)(threadControlBlockBytes))(name + "_block")
                                                    in
                                                        let _ = parStore(builder)(i64)(i8)(ptrType)(block)(0)(block)(name + "_block_self")
                                                        in
                                                            let chunk =
                                                                emitArenaOsAllocate(context)(function_)(builder)(i64)(arena)(parConst(i64)(4194304))(name + "_chunk")
                                                            in
                                                                let _ =
                                                                    emitRegionChunkSetup(builder)(i64)(i8)(ptrType)(memOffsetPtr(builder)(i64)(i8)(ptrType)(block)(8)(name + "_cursor_slot"))(memOffsetPtr(builder)(i64)(i8)(ptrType)(block)(16)(name + "_end_slot"))(chunk)(parConst(i64)(4194304))(parConst(i64)(0))(name + "_chunk")
                                                                in
                                                                    let _ = parStore(builder)(i64)(i8)(ptrType)(desc)(descWorkerBlock)(block)(name + "_store_block")
                                                                    in
                                                                        let _ =
                                                                            parStore(builder)(i64)(i8)(ptrType)(desc)(descMode)(parConst(i64)(1))(name + "_mode_worker")
                                                                        in
                                                                            let stack =
                                                                                emitArenaOsAllocate(context)(function_)(builder)(i64)(arena)(parConst(i64)(workerStackBytes))(name + "_stack")
                                                                            in
                                                                                let _ = parStore(builder)(i64)(i8)(ptrType)(desc)(descWorkerStack)(stack)(name + "_store_stack")
                                                                                in
                                                                                    let _ =
                                                                                        parStore(builder)(i64)(i8)(ptrType)(desc)(descExited)(parConst(i64)(1))(name + "_running")
                                                                                    in
                                                                                        let _ =
                                                                                            emitCloneWorker(context)(builder)(i64)(desc)(buildAdd(builder)(stack)(parConst(i64)(workerStackBytes))(name + "_stack_top"))(runtime.workerFn)
                                                                                        in
                                                                                            let _ = buildBr(builder)(mergeBlock)
                                                                                            in
                                                                                                let _ = positionBuilderAtEnd(builder)(inlineBlock)
                                                                                                in
                                                                                                    let _ =
                                                                                                        buildAtomicRmw(builder)(atomicRmwAdd)(activeCounter)(parConst(i64)(-1))(atomicOrderingSequentiallyConsistent)
                                                                                                    in
                                                                                                        let _ = emitForkInline(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(closureFnType)(desc)(rightClosure)
                                                                                                        in
                                                                                                            let _ = buildBr(builder)(mergeBlock)
                                                                                                            in
                                                                                                                let _ = positionBuilderAtEnd(builder)(mergeBlock)
                                                                                                                in desc)

// Stage 0's `EmitParallelJoin`: waits until the done word is set (a `FUTEX_WAIT` re-checks it, so
// a wake that races the check is not lost) and answers the thunk's result.
let emitParallelJoin context function_ builder i64 i8 ptrType desc name =
    (let checkBlock = appendBasicBlock(context)(function_)(name + "_check")
    in
        let waitBlock = appendBasicBlock(context)(function_)(name + "_wait")
        in
            let doneBlock = appendBasicBlock(context)(function_)(name + "_done")
            in
                let _ = buildBr(builder)(checkBlock)
                in
                    let _ = positionBuilderAtEnd(builder)(checkBlock)
                    in
                        let doneWord = parWord(builder)(i64)(i8)(ptrType)(desc)(descDone)(name + "_done_word")
                        in
                            let _ =
                                buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(doneWord)(parConst(i64)(0))(name + "_is_done"))(doneBlock)(waitBlock)
                            in
                                let _ = positionBuilderAtEnd(builder)(waitBlock)
                                in
                                    let _ = emitFutex(builder)(i64)(desc)(futexWaitPrivate)(0)(name + "_futex")
                                    in
                                        let _ = buildBr(builder)(checkBlock)
                                        in
                                            let _ = positionBuilderAtEnd(builder)(doneBlock)
                                            in parWord(builder)(i64)(i8)(ptrType)(desc)(descResult)(name))

// Unmaps every arena chunk of a finished worker, from its current chunk back through the header
// links, as stage 0's `EmitFreeWorkerArenaChunks`.
let emitFreeWorkerChunks context function_ builder i64 i8 ptrType arenaEnd name =
    (let endSlot = buildEntryAlloca(builder)(i64)(name + "_end_slot")
    in
        let loopBlock = appendBasicBlock(context)(function_)(name + "_loop")
        in
            let releaseBlock = appendBasicBlock(context)(function_)(name + "_release")
            in
                let doneBlock = appendBasicBlock(context)(function_)(name + "_done")
                in
                    let _ = buildStore(builder)(arenaEnd)(endSlot)
                    in
                        let _ = buildBr(builder)(loopBlock)
                        in
                            let _ = positionBuilderAtEnd(builder)(loopBlock)
                            in
                                let currentEnd = buildLoad(builder)(i64)(endSlot)(name + "_end")
                                in
                                    let _ =
                                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(currentEnd)(parConst(i64)(0))(name + "_none"))(doneBlock)(releaseBlock)
                                    in
                                        let _ = positionBuilderAtEnd(builder)(releaseBlock)
                                        in
                                            let base = parWord(builder)(i64)(i8)(ptrType)(currentEnd)(0)(name + "_base")
                                            in
                                                let previousEnd = parWord(builder)(i64)(i8)(ptrType)(base)(0)(name + "_previous_end")
                                                in
                                                    let size =
                                                        buildSub(builder)(buildAdd(builder)(currentEnd)(parConst(i64)(chunkFooterBytes))(name + "_top"))(base)(name + "_size")
                                                    in
                                                        let _ = emitLinuxMunmap(builder)(i64)(base)(size)
                                                        in
                                                            let _ = buildStore(builder)(previousEnd)(endSlot)
                                                            in
                                                                let _ = buildBr(builder)(loopBlock)
                                                                in positionBuilderAtEnd(builder)(doneBlock))

// Stage 0's `EmitParallelCleanup`: for a thunk that ran on a worker, waits until the kernel has
// cleared the exit word (the thread no longer uses its stack), then unmaps the stack, the worker's
// arena chunks and its control block. An inline run has nothing to release.
let emitParallelCleanup context function_ builder i64 i8 ptrType desc name =
    (let freeBlock = appendBasicBlock(context)(function_)(name + "_free")
    in
        let exitCheckBlock = appendBasicBlock(context)(function_)(name + "_exit_check")
        in
            let exitWaitBlock = appendBasicBlock(context)(function_)(name + "_exit_wait")
            in
                let exitedBlock = appendBasicBlock(context)(function_)(name + "_exited")
                in
                    let doneBlock = appendBasicBlock(context)(function_)(name + "_done")
                    in
                        let mode = parWord(builder)(i64)(i8)(ptrType)(desc)(descMode)(name + "_mode")
                        in
                            let _ =
                                buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(mode)(parConst(i64)(0))(name + "_on_worker"))(freeBlock)(doneBlock)
                            in
                                let _ = positionBuilderAtEnd(builder)(freeBlock)
                                in
                                    let _ = buildBr(builder)(exitCheckBlock)
                                    in
                                        let _ = positionBuilderAtEnd(builder)(exitCheckBlock)
                                        in
                                            let exited = parWord(builder)(i64)(i8)(ptrType)(desc)(descExited)(name + "_exit_word")
                                            in
                                                let _ =
                                                    buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(exited)(parConst(i64)(0))(name + "_running"))(exitWaitBlock)(exitedBlock)
                                                in
                                                    let _ = positionBuilderAtEnd(builder)(exitWaitBlock)
                                                    in
                                                        let _ =
                                                            emitFutex(builder)(i64)(buildAdd(builder)(desc)(parConst(i64)(descExited))(name + "_exit_address"))(futexWait)(1)(name + "_exit_futex")
                                                        in
                                                            let _ = buildBr(builder)(exitCheckBlock)
                                                            in
                                                                let _ = positionBuilderAtEnd(builder)(exitedBlock)
                                                                in
                                                                    let _ =
                                                                        workerStackBytes
                                                                        |> parConst(i64)
                                                                        |> emitLinuxMunmap(builder)(i64)(parWord(builder)(i64)(i8)(ptrType)(desc)(descWorkerStack)(name + "_stack"))
                                                                    in
                                                                        let block = parWord(builder)(i64)(i8)(ptrType)(desc)(descWorkerBlock)(name + "_block")
                                                                        in
                                                                            let _ =
                                                                                emitFreeWorkerChunks(context)(function_)(builder)(i64)(i8)(ptrType)(parWord(builder)(i64)(i8)(ptrType)(block)(16)(name + "_arena_end"))(name + "_chunks")
                                                                            in
                                                                                let _ =
                                                                                    threadControlBlockBytes
                                                                                    |> parConst(i64)
                                                                                    |> emitLinuxMunmap(builder)(i64)(block)
                                                                                in
                                                                                    let _ = buildBr(builder)(doneBlock)
                                                                                    in positionBuilderAtEnd(builder)(doneBlock))

// The work-conserving queue region, stage 0's layout: a 64-byte header (next element index,
// element count, mapper, spawned workers, region size, combine, next merge task, merge-tree item
// count), the snapshotted elements, an item word and a flag word per merge-tree node, and a 72-byte
// record per worker (the queue at 0; stack, control block and exit word at the fork descriptor's
// offsets).
let queueNextIndex = 0

let queueCount = 8

let queueMapper = 16

let queueWorkerCount = 24

let queueRegionBytes = 32

let queueCombine = 40

let queueNextMergeTask = 48

let queueTotalItems = 56

let queueHeaderBytes = 64

let queueRecordBytes = 72

// The flag at `flagAddress` read with an acquire (an atomic add of 0), waited on with a futex until
// it is set.
let emitQueueFlagWait context function_ builder i64 flagAddress name =
    (let checkBlock = appendBasicBlock(context)(function_)(name + "_check")
    in
        let waitBlock = appendBasicBlock(context)(function_)(name + "_wait")
        in
            let readyBlock = appendBasicBlock(context)(function_)(name + "_ready")
            in
                let _ = buildBr(builder)(checkBlock)
                in
                    let _ = positionBuilderAtEnd(builder)(checkBlock)
                    in
                        let flag =
                            buildAtomicRmw(builder)(atomicRmwAdd)(buildIntToPtr(builder)(flagAddress)(pointerType(context)(0u32))(name + "_flag_ptr"))(parConst(i64)(0))(atomicOrderingSequentiallyConsistent)
                        in
                            let _ =
                                buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(flag)(parConst(i64)(0))(name + "_is_ready"))(readyBlock)(waitBlock)
                            in
                                let _ = positionBuilderAtEnd(builder)(waitBlock)
                                in
                                    let _ = emitFutex(builder)(i64)(flagAddress)(futexWaitPrivate)(0)(name + "_futex")
                                    in
                                        let _ = buildBr(builder)(checkBlock)
                                        in positionBuilderAtEnd(builder)(readyBlock))

// Publishes an item: the value is stored, then its flag is set with an atomic add and its one
// waiter woken.
let emitQueuePublish context builder i64 i8 ptrType itemAddress flagAddress value name =
    (let _ = parStore(builder)(i64)(i8)(ptrType)(itemAddress)(0)(value)(name + "_item")
    in
        let _ =
            buildAtomicRmw(builder)(atomicRmwAdd)(buildIntToPtr(builder)(flagAddress)(pointerType(context)(0u32))(name + "_flag_ptr"))(parConst(i64)(1))(atomicOrderingSequentiallyConsistent)
        in emitFutex(builder)(i64)(flagAddress)(futexWakePrivate)(1)(name + "_wake"))

let atomicWordAdd context builder i64 address (delta: Int) name =
    buildAtomicRmw(builder)(atomicRmwAdd)(buildIntToPtr(builder)(address)(pointerType(context)(0u32))(name))(parConst(i64)(delta))(atomicOrderingSequentiallyConsistent)

// Stage 0's `__ashes_parallel_queue_drain(queue)`: claims element indexes and publishes
// `f(element)` for each, then claims merge tasks round-major, waits for each task's operands and
// publishes `combine(left)(right)`, or the left one alone when the round has no right operand.
let defineQueueDrainFunction module_ context builder i64 i8 ptrType closureFnType =
    (let drainType =
        functionType(voidType(context))([i64])(1u32)(false)
    in
        let drainFn = addInternalFunction(module_)("__ashes_parallel_queue_drain")(drainType)
        in
            let block =
                given (name) -> appendBasicBlock(context)(drainFn)(name)
            in
                let _ =
                    "entry"
                    |> block
                    |> positionBuilderAtEnd(builder)
                in
                    let desc = getParam(drainFn)(0u32)
                    in
                        let count = parWord(builder)(i64)(i8)(ptrType)(desc)(queueCount)("parq_n")
                        in
                            let mapper = parWord(builder)(i64)(i8)(ptrType)(desc)(queueMapper)("parq_f")
                            in
                                let combine = parWord(builder)(i64)(i8)(ptrType)(desc)(queueCombine)("parq_combine")
                                in
                                    let totalItems = parWord(builder)(i64)(i8)(ptrType)(desc)(queueTotalItems)("parq_s")
                                    in
                                        let elemsBase =
                                            buildAdd(builder)(desc)(parConst(i64)(queueHeaderBytes))("parq_elems")
                                        in
                                            let itemsBase =
                                                buildAdd(builder)(elemsBase)(buildMul(builder)(count)(parConst(i64)(8))("parq_n_bytes"))("parq_items")
                                            in
                                                let flagsBase =
                                                    buildAdd(builder)(itemsBase)(buildMul(builder)(totalItems)(parConst(i64)(8))("parq_s_bytes"))("parq_flags")
                                                in
                                                    let roundCountSlot = buildAlloca(builder)(i64)("parq_round_count")
                                                    in
                                                        let previousBaseSlot = buildAlloca(builder)(i64)("parq_previous_base")
                                                        in
                                                            let currentBaseSlot = buildAlloca(builder)(i64)("parq_current_base")
                                                            in
                                                                let remainderSlot = buildAlloca(builder)(i64)("parq_remainder")
                                                                in
                                                                    let resultSlot = buildAlloca(builder)(i64)("parq_result")
                                                                    in
                                                                        let leafLoop = block("parq_leaf_loop")
                                                                        in
                                                                            let leafBody = block("parq_leaf_body")
                                                                            in
                                                                                let mergeClaim = block("parq_merge_claim")
                                                                                in
                                                                                    let locateLoop = block("parq_locate")
                                                                                    in
                                                                                        let locateNext = block("parq_locate_next")
                                                                                        in
                                                                                            let located = block("parq_located")
                                                                                            in
                                                                                                let combineBlock = block("parq_combine")
                                                                                                in
                                                                                                    let promoteBlock = block("parq_promote")
                                                                                                    in
                                                                                                        let publishBlock = block("parq_publish")
                                                                                                        in
                                                                                                            let doneBlock = block("parq_done")
                                                                                                            in
                                                                                                                let _ = buildBr(builder)(leafLoop)
                                                                                                                in
                                                                                                                    let _ = positionBuilderAtEnd(builder)(leafLoop)
                                                                                                                    in
                                                                                                                        let index = atomicWordAdd(context)(builder)(i64)(desc)(1)("parq_claim")
                                                                                                                        in
                                                                                                                            let _ =
                                                                                                                                buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(index)(count)("parq_has_leaf"))(leafBody)(mergeClaim)
                                                                                                                            in
                                                                                                                                let _ = positionBuilderAtEnd(builder)(leafBody)
                                                                                                                                in
                                                                                                                                    let indexBytes =
                                                                                                                                        buildMul(builder)(index)(parConst(i64)(8))("parq_index_bytes")
                                                                                                                                    in
                                                                                                                                        let element =
                                                                                                                                            parWord(builder)(i64)(i8)(ptrType)(buildAdd(builder)(elemsBase)(indexBytes)("parq_element_address"))(0)("parq_element")
                                                                                                                                        in
                                                                                                                                            let mapped =
                                                                                                                                                emitCallClosure(builder)(i64)(i8)(ptrType)(closureFnType)(mapper)(element)(parConst(i64)(0))("parq_mapped")
                                                                                                                                            in
                                                                                                                                                let _ =
                                                                                                                                                    emitQueuePublish(context)(builder)(i64)(i8)(ptrType)(buildAdd(builder)(itemsBase)(indexBytes)("parq_leaf_item"))(buildAdd(builder)(flagsBase)(indexBytes)("parq_leaf_flag"))(mapped)("parq_leaf")
                                                                                                                                                in
                                                                                                                                                    let _ = buildBr(builder)(leafLoop)
                                                                                                                                                    in
                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(mergeClaim)
                                                                                                                                                        in
                                                                                                                                                            let task =
                                                                                                                                                                atomicWordAdd(context)(builder)(i64)(buildAdd(builder)(desc)(parConst(i64)(queueNextMergeTask))("parq_merge_counter"))(1)("parq_claim_task")
                                                                                                                                                            in
                                                                                                                                                                let _ = buildStore(builder)(count)(roundCountSlot)
                                                                                                                                                                in
                                                                                                                                                                    let _ =
                                                                                                                                                                        buildStore(builder)(parConst(i64)(0))(previousBaseSlot)
                                                                                                                                                                    in
                                                                                                                                                                        let _ = buildStore(builder)(count)(currentBaseSlot)
                                                                                                                                                                        in
                                                                                                                                                                            let _ = buildStore(builder)(task)(remainderSlot)
                                                                                                                                                                            in
                                                                                                                                                                                let _ =
                                                                                                                                                                                    buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(task)(buildSub(builder)(totalItems)(count)("parq_task_total"))("parq_has_task"))(locateLoop)(doneBlock)
                                                                                                                                                                                in
                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(locateLoop)
                                                                                                                                                                                    in
                                                                                                                                                                                        let roundCount = buildLoad(builder)(i64)(roundCountSlot)("parq_c")
                                                                                                                                                                                        in
                                                                                                                                                                                            let roundSize =
                                                                                                                                                                                                buildLShr(builder)(buildAdd(builder)(roundCount)(parConst(i64)(1))("parq_c1"))(parConst(i64)(1))("parq_round_size")
                                                                                                                                                                                            in
                                                                                                                                                                                                let remainder = buildLoad(builder)(i64)(remainderSlot)("parq_rem")
                                                                                                                                                                                                in
                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                        buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(remainder)(roundSize)("parq_in_round"))(located)(locateNext)
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(locateNext)
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                buildStore(builder)(buildSub(builder)(remainder)(roundSize)("parq_rem_next"))(remainderSlot)
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let currentBase = buildLoad(builder)(i64)(currentBaseSlot)("parq_current_base_value")
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let _ = buildStore(builder)(currentBase)(previousBaseSlot)
                                                                                                                                                                                                                    in
                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                            buildStore(builder)(buildAdd(builder)(currentBase)(roundSize)("parq_next_base"))(currentBaseSlot)
                                                                                                                                                                                                                        in
                                                                                                                                                                                                                            let _ = buildStore(builder)(roundSize)(roundCountSlot)
                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                let _ = buildBr(builder)(locateLoop)
                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(located)
                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                        let previousCount = buildLoad(builder)(i64)(roundCountSlot)("parq_previous_count")
                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                            let slot = buildLoad(builder)(i64)(remainderSlot)("parq_j")
                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                let leftOffset =
                                                                                                                                                                                                                                                    buildMul(builder)(slot)(parConst(i64)(2))("parq_left_offset")
                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                    let leftBytes =
                                                                                                                                                                                                                                                        buildMul(builder)(buildAdd(builder)(buildLoad(builder)(i64)(previousBaseSlot)("parq_previous_base_value"))(leftOffset)("parq_left_item"))(parConst(i64)(8))("parq_left_bytes")
                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                        let outBytes =
                                                                                                                                                                                                                                                            buildMul(builder)(buildAdd(builder)(buildLoad(builder)(i64)(currentBaseSlot)("parq_out_base"))(slot)("parq_out_item"))(parConst(i64)(8))("parq_out_bytes")
                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                emitQueueFlagWait(context)(drainFn)(builder)(i64)(buildAdd(builder)(flagsBase)(leftBytes)("parq_left_flag"))("parq_left")
                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                let left =
                                                                                                                                                                                                                                                                    parWord(builder)(i64)(i8)(ptrType)(buildAdd(builder)(itemsBase)(leftBytes)("parq_left_address"))(0)("parq_left_value")
                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                    let hasRight =
                                                                                                                                                                                                                                                                        buildICmp(builder)(intPredicateSlt)(buildAdd(builder)(leftOffset)(parConst(i64)(1))("parq_right_offset"))(previousCount)("parq_has_right")
                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                        let _ = buildCondBr(builder)(hasRight)(combineBlock)(promoteBlock)
                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(combineBlock)
                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                let rightBytes =
                                                                                                                                                                                                                                                                                    buildAdd(builder)(leftBytes)(parConst(i64)(8))("parq_right_bytes")
                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                                                        emitQueueFlagWait(context)(drainFn)(builder)(i64)(buildAdd(builder)(flagsBase)(rightBytes)("parq_right_flag"))("parq_right")
                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                        let right =
                                                                                                                                                                                                                                                                                            parWord(builder)(i64)(i8)(ptrType)(buildAdd(builder)(itemsBase)(rightBytes)("parq_right_address"))(0)("parq_right_value")
                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                            let partial =
                                                                                                                                                                                                                                                                                                emitCallClosure(builder)(i64)(i8)(ptrType)(closureFnType)(combine)(left)(parConst(i64)(0))("parq_partial")
                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                let merged =
                                                                                                                                                                                                                                                                                                    emitCallClosure(builder)(i64)(i8)(ptrType)(closureFnType)(partial)(right)(parConst(i64)(0))("parq_merged")
                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                    let _ = buildStore(builder)(merged)(resultSlot)
                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                        let _ = buildBr(builder)(publishBlock)
                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(promoteBlock)
                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                let _ = buildStore(builder)(left)(resultSlot)
                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                    let _ = buildBr(builder)(publishBlock)
                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(publishBlock)
                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                                emitQueuePublish(context)(builder)(i64)(i8)(ptrType)(buildAdd(builder)(itemsBase)(outBytes)("parq_out_item_address"))(buildAdd(builder)(flagsBase)(outBytes)("parq_out_flag"))(buildLoad(builder)(i64)(resultSlot)("parq_result_value"))("parq_merge")
                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                let _ = buildBr(builder)(mergeClaim)
                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                        let _ = buildRetVoid(builder)
                                                                                                                                                                                                                                                                                                                                        in (drainFn, drainType))

// Stage 0's `__ashes_parallel_queue_worker(record)`: makes the record's control block this thread's
// GS base, drains the queue and gives its worker slot back.
let defineQueueWorkerFunction module_ context builder i64 i8 ptrType drainFn drainType activeCounter =
    (let workerType =
        functionType(voidType(context))([i64])(1u32)(false)
    in
        let workerFn = addInternalFunction(module_)("__ashes_parallel_queue_worker")(workerType)
        in
            let _ =
                "entry"
                |> appendBasicBlock(context)(workerFn)
                |> positionBuilderAtEnd(builder)
            in
                let record = getParam(workerFn)(0u32)
                in
                    let _ =
                        emitLinuxSyscallCall(builder)(i64)(parConst(i64)(syscallArchPrctl))(parConst(i64)(archSetGs))(parWord(builder)(i64)(i8)(ptrType)(record)(descWorkerBlock)("parq_worker_block"))(parConst(i64)(0))("parq_worker_set_gs")
                    in
                        let _ = buildCall(builder)(drainType)(drainFn)([parWord(builder)(i64)(i8)(ptrType)(record)(0)("parq_worker_queue")])(1u32)("")
                        in
                            let _ =
                                buildAtomicRmw(builder)(atomicRmwAdd)(activeCounter)(parConst(i64)(-1))(atomicOrderingSequentiallyConsistent)
                            in
                                let _ = buildRetVoid(builder)
                                in workerFn)

// A worker's control block wired to a fresh arena chunk, and its mapped stack, recorded in the
// worker record (or fork descriptor) at `record`; answers the stack's top.
let emitPrepareWorker context function_ builder i64 i8 ptrType (arena: ArenaRuntime) record name =
    (let block =
        emitArenaOsAllocate(context)(function_)(builder)(i64)(arena)(parConst(i64)(threadControlBlockBytes))(name + "_block")
    in
        let _ = parStore(builder)(i64)(i8)(ptrType)(block)(0)(block)(name + "_block_self")
        in
            let chunk =
                emitArenaOsAllocate(context)(function_)(builder)(i64)(arena)(parConst(i64)(4194304))(name + "_chunk")
            in
                let _ =
                    emitRegionChunkSetup(builder)(i64)(i8)(ptrType)(memOffsetPtr(builder)(i64)(i8)(ptrType)(block)(8)(name + "_cursor_slot"))(memOffsetPtr(builder)(i64)(i8)(ptrType)(block)(16)(name + "_end_slot"))(chunk)(parConst(i64)(4194304))(parConst(i64)(0))(name + "_chunk")
                in
                    let _ = parStore(builder)(i64)(i8)(ptrType)(record)(descWorkerBlock)(block)(name + "_store_block")
                    in
                        let stack =
                            emitArenaOsAllocate(context)(function_)(builder)(i64)(arena)(parConst(i64)(workerStackBytes))(name + "_stack")
                        in
                            let _ = parStore(builder)(i64)(i8)(ptrType)(record)(descWorkerStack)(stack)(name + "_store_stack")
                            in
                                let _ =
                                    parStore(builder)(i64)(i8)(ptrType)(record)(descExited)(parConst(i64)(1))(name + "_running")
                                in
                                    buildAdd(builder)(stack)(parConst(i64)(workerStackBytes))(name + "_stack_top"))

// Waits until the kernel has cleared the exit word of the worker whose record is at `record`, then
// unmaps its stack, its arena chunks and its control block.
let emitReleaseWorker context function_ builder i64 i8 ptrType record name =
    (let exitCheckBlock = appendBasicBlock(context)(function_)(name + "_exit_check")
    in
        let exitWaitBlock = appendBasicBlock(context)(function_)(name + "_exit_wait")
        in
            let exitedBlock = appendBasicBlock(context)(function_)(name + "_exited")
            in
                let _ = buildBr(builder)(exitCheckBlock)
                in
                    let _ = positionBuilderAtEnd(builder)(exitCheckBlock)
                    in
                        let exited = parWord(builder)(i64)(i8)(ptrType)(record)(descExited)(name + "_exit_word")
                        in
                            let _ =
                                buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(exited)(parConst(i64)(0))(name + "_running"))(exitWaitBlock)(exitedBlock)
                            in
                                let _ = positionBuilderAtEnd(builder)(exitWaitBlock)
                                in
                                    let _ =
                                        emitFutex(builder)(i64)(buildAdd(builder)(record)(parConst(i64)(descExited))(name + "_exit_address"))(futexWait)(1)(name + "_exit_futex")
                                    in
                                        let _ = buildBr(builder)(exitCheckBlock)
                                        in
                                            let _ = positionBuilderAtEnd(builder)(exitedBlock)
                                            in
                                                let _ =
                                                    workerStackBytes
                                                    |> parConst(i64)
                                                    |> emitLinuxMunmap(builder)(i64)(parWord(builder)(i64)(i8)(ptrType)(record)(descWorkerStack)(name + "_stack"))
                                                in
                                                    let block = parWord(builder)(i64)(i8)(ptrType)(record)(descWorkerBlock)(name + "_block")
                                                    in
                                                        let _ =
                                                            emitFreeWorkerChunks(context)(function_)(builder)(i64)(i8)(ptrType)(parWord(builder)(i64)(i8)(ptrType)(block)(16)(name + "_arena_end"))(name + "_chunks")
                                                        in
                                                            threadControlBlockBytes
                                                            |> parConst(i64)
                                                            |> emitLinuxMunmap(builder)(i64)(block))

// Stage 0's `__ashes_parallel_queue_start(f, combine, list)`: counts the list, sizes the merge tree
// (`n + ceil(n/2) + ... + 1` items), maps the zeroed region and snapshots the elements into it,
// then claims worker slots up to the cap and the element count and starts a worker per slot. With
// no worker at all the caller drains the whole queue itself. Answers the queue.
let defineQueueStartFunction module_ context builder i64 i8 ptrType (arena: ArenaRuntime) drainFn drainType workerFn activeCounter capFn capType overrideGlobal =
    (let startType = functionType(i64)([i64, i64, i64])(3u32)(false)
    in
        let startFn = addInternalFunction(module_)("__ashes_parallel_queue_start")(startType)
        in
            let block =
                given (name) -> appendBasicBlock(context)(startFn)(name)
            in
                let _ =
                    "entry"
                    |> block
                    |> positionBuilderAtEnd(builder)
                in
                    let mapper = getParam(startFn)(0u32)
                    in
                        let combine = getParam(startFn)(1u32)
                        in
                            let list = getParam(startFn)(2u32)
                            in
                                let cursorSlot = buildAlloca(builder)(i64)("parq_cursor")
                                in
                                    let countSlot = buildAlloca(builder)(i64)("parq_count")
                                    in
                                        let roundSlot = buildAlloca(builder)(i64)("parq_round")
                                        in
                                            let totalSlot = buildAlloca(builder)(i64)("parq_total")
                                            in
                                                let indexSlot = buildAlloca(builder)(i64)("parq_index")
                                                in
                                                    let spawnedSlot = buildAlloca(builder)(i64)("parq_spawned")
                                                    in
                                                        let countLoop = block("parq_count_loop")
                                                        in
                                                            let countBody = block("parq_count_body")
                                                            in
                                                                let sizeLoop = block("parq_size_loop")
                                                                in
                                                                    let sizeBody = block("parq_size_body")
                                                                    in
                                                                        let regionBlock = block("parq_region")
                                                                        in
                                                                            let fillLoop = block("parq_fill_loop")
                                                                            in
                                                                                let fillBody = block("parq_fill_body")
                                                                                in
                                                                                    let spawnLoop = block("parq_spawn_loop")
                                                                                    in
                                                                                        let spawnTry = block("parq_spawn_try")
                                                                                        in
                                                                                            let spawnBody = block("parq_spawn_body")
                                                                                            in
                                                                                                let spawnAbort = block("parq_spawn_abort")
                                                                                                in
                                                                                                    let spawnDone = block("parq_spawn_done")
                                                                                                    in
                                                                                                        let drainInline = block("parq_drain_inline")
                                                                                                        in
                                                                                                            let returnBlock = block("parq_return")
                                                                                                            in
                                                                                                                let _ = buildStore(builder)(list)(cursorSlot)
                                                                                                                in
                                                                                                                    let _ =
                                                                                                                        buildStore(builder)(parConst(i64)(0))(countSlot)
                                                                                                                    in
                                                                                                                        let _ = buildBr(builder)(countLoop)
                                                                                                                        in
                                                                                                                            let _ = positionBuilderAtEnd(builder)(countLoop)
                                                                                                                            in
                                                                                                                                let countCursor = buildLoad(builder)(i64)(cursorSlot)("parq_count_cursor")
                                                                                                                                in
                                                                                                                                    let _ =
                                                                                                                                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(countCursor)(parConst(i64)(0))("parq_count_end"))(sizeLoop)(countBody)
                                                                                                                                    in
                                                                                                                                        let _ = positionBuilderAtEnd(builder)(countBody)
                                                                                                                                        in
                                                                                                                                            let _ =
                                                                                                                                                buildStore(builder)(buildAdd(builder)(buildLoad(builder)(i64)(countSlot)("parq_count_value"))(parConst(i64)(1))("parq_count_next"))(countSlot)
                                                                                                                                            in
                                                                                                                                                let _ =
                                                                                                                                                    buildStore(builder)(parWord(builder)(i64)(i8)(ptrType)(countCursor)(8)("parq_count_tail"))(cursorSlot)
                                                                                                                                                in
                                                                                                                                                    let _ = buildBr(builder)(countLoop)
                                                                                                                                                    in
                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(sizeLoop)
                                                                                                                                                        in
                                                                                                                                                            let count = buildLoad(builder)(i64)(countSlot)("parq_n")
                                                                                                                                                            in
                                                                                                                                                                let _ = buildStore(builder)(count)(roundSlot)
                                                                                                                                                                in
                                                                                                                                                                    let _ = buildStore(builder)(count)(totalSlot)
                                                                                                                                                                    in
                                                                                                                                                                        let sizeCheck = block("parq_size_check")
                                                                                                                                                                        in
                                                                                                                                                                            let _ = buildBr(builder)(sizeCheck)
                                                                                                                                                                            in
                                                                                                                                                                                let _ = positionBuilderAtEnd(builder)(sizeCheck)
                                                                                                                                                                                in
                                                                                                                                                                                    let currentRound = buildLoad(builder)(i64)(roundSlot)("parq_round_value")
                                                                                                                                                                                    in
                                                                                                                                                                                        let _ =
                                                                                                                                                                                            buildCondBr(builder)(buildICmp(builder)(intPredicateSgt)(currentRound)(parConst(i64)(1))("parq_more_rounds"))(sizeBody)(regionBlock)
                                                                                                                                                                                        in
                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(sizeBody)
                                                                                                                                                                                            in
                                                                                                                                                                                                let nextRound =
                                                                                                                                                                                                    buildLShr(builder)(buildAdd(builder)(currentRound)(parConst(i64)(1))("parq_round_plus"))(parConst(i64)(1))("parq_next_round")
                                                                                                                                                                                                in
                                                                                                                                                                                                    let _ = buildStore(builder)(nextRound)(roundSlot)
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                            buildStore(builder)(buildAdd(builder)(buildLoad(builder)(i64)(totalSlot)("parq_total_value"))(nextRound)("parq_total_next"))(totalSlot)
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let _ = buildBr(builder)(sizeCheck)
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let _ = positionBuilderAtEnd(builder)(regionBlock)
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let totalItems = buildLoad(builder)(i64)(totalSlot)("parq_s")
                                                                                                                                                                                                                    in
                                                                                                                                                                                                                        let cap = emitWorkerCap(builder)(i64)(capFn)(capType)(overrideGlobal)
                                                                                                                                                                                                                        in
                                                                                                                                                                                                                            let maxWorkers =
                                                                                                                                                                                                                                buildSelect(builder)(buildICmp(builder)(intPredicateSlt)(cap)(count)("parq_cap_below_n"))(cap)(count)("parq_max_workers")
                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                let payloadBytes =
                                                                                                                                                                                                                                    buildAdd(builder)(buildMul(builder)(count)(parConst(i64)(8))("parq_element_bytes"))(buildMul(builder)(totalItems)(parConst(i64)(16))("parq_tree_bytes"))("parq_payload_bytes")
                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                    let regionBytes =
                                                                                                                                                                                                                                        buildAdd(builder)(buildAdd(builder)(parConst(i64)(queueHeaderBytes))(payloadBytes)("parq_header_payload"))(buildMul(builder)(maxWorkers)(parConst(i64)(queueRecordBytes))("parq_record_bytes"))("parq_region_bytes")
                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                        let desc = emitArenaOsAllocate(context)(startFn)(builder)(i64)(arena)(regionBytes)("parq_region")
                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                            let _ = parStore(builder)(i64)(i8)(ptrType)(desc)(queueCount)(count)("parq_store_n")
                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                let _ = parStore(builder)(i64)(i8)(ptrType)(desc)(queueMapper)(mapper)("parq_store_f")
                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                    let _ = parStore(builder)(i64)(i8)(ptrType)(desc)(queueCombine)(combine)("parq_store_combine")
                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                        let _ = parStore(builder)(i64)(i8)(ptrType)(desc)(queueTotalItems)(totalItems)("parq_store_s")
                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                            let _ = parStore(builder)(i64)(i8)(ptrType)(desc)(queueRegionBytes)(regionBytes)("parq_store_size")
                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                let elemsBase =
                                                                                                                                                                                                                                                                    buildAdd(builder)(desc)(parConst(i64)(queueHeaderBytes))("parq_elems_base")
                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                    let _ = buildStore(builder)(list)(cursorSlot)
                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                                            buildStore(builder)(parConst(i64)(0))(indexSlot)
                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                            let _ = buildBr(builder)(fillLoop)
                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                let _ = positionBuilderAtEnd(builder)(fillLoop)
                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                    let fillCursor = buildLoad(builder)(i64)(cursorSlot)("parq_fill_cursor")
                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                                                            buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(fillCursor)(parConst(i64)(0))("parq_fill_end"))(spawnLoop)(fillBody)
                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(fillBody)
                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                let fillIndex = buildLoad(builder)(i64)(indexSlot)("parq_fill_index")
                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                    let _ =
                                                                                                                                                                                                                                                                                                        parStore(builder)(i64)(i8)(ptrType)(buildAdd(builder)(elemsBase)(buildMul(builder)(fillIndex)(parConst(i64)(8))("parq_fill_offset"))("parq_fill_address"))(0)(parWord(builder)(i64)(i8)(ptrType)(fillCursor)(0)("parq_fill_head"))("parq_fill_element")
                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                                                                            buildStore(builder)(parWord(builder)(i64)(i8)(ptrType)(fillCursor)(8)("parq_fill_tail"))(cursorSlot)
                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                buildStore(builder)(buildAdd(builder)(fillIndex)(parConst(i64)(1))("parq_fill_next"))(indexSlot)
                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                let _ = buildBr(builder)(fillLoop)
                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(spawnLoop)
                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                                                                                            buildStore(builder)(parConst(i64)(0))(spawnedSlot)
                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                            let spawnCheck = block("parq_spawn_check")
                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                let _ = buildBr(builder)(spawnCheck)
                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(spawnCheck)
                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                        let spawned = buildLoad(builder)(i64)(spawnedSlot)("parq_spawned_value")
                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                                                buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(spawned)(maxWorkers)("parq_want_more"))(spawnTry)(spawnDone)
                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                let _ = positionBuilderAtEnd(builder)(spawnTry)
                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                    let previous =
                                                                                                                                                                                                                                                                                                                                                        buildAtomicRmw(builder)(atomicRmwAdd)(activeCounter)(parConst(i64)(1))(atomicOrderingSequentiallyConsistent)
                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                        let _ =
                                                                                                                                                                                                                                                                                                                                                            buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(previous)(cap)("parq_can_spawn"))(spawnBody)(spawnAbort)
                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                            let _ = positionBuilderAtEnd(builder)(spawnAbort)
                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                                                                                                                                                    buildAtomicRmw(builder)(atomicRmwAdd)(activeCounter)(parConst(i64)(-1))(atomicOrderingSequentiallyConsistent)
                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                    let _ = buildBr(builder)(spawnDone)
                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(spawnBody)
                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                            let record =
                                                                                                                                                                                                                                                                                                                                                                                buildAdd(builder)(buildAdd(builder)(elemsBase)(payloadBytes)("parq_records"))(buildMul(builder)(spawned)(parConst(i64)(queueRecordBytes))("parq_record_offset"))("parq_record")
                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                let _ = parStore(builder)(i64)(i8)(ptrType)(record)(0)(desc)("parq_record_queue")
                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                    let stackTop = emitPrepareWorker(context)(startFn)(builder)(i64)(i8)(ptrType)(arena)(record)("parq_worker")
                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                        let _ = emitCloneWorker(context)(builder)(i64)(record)(stackTop)(workerFn)
                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                            let _ =
                                                                                                                                                                                                                                                                                                                                                                                                buildStore(builder)(buildAdd(builder)(spawned)(parConst(i64)(1))("parq_spawned_next"))(spawnedSlot)
                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                let _ = buildBr(builder)(spawnCheck)
                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(spawnDone)
                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                        let spawnedTotal = buildLoad(builder)(i64)(spawnedSlot)("parq_spawned_total")
                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                            let _ = parStore(builder)(i64)(i8)(ptrType)(desc)(queueWorkerCount)(spawnedTotal)("parq_store_workers")
                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                let _ =
                                                                                                                                                                                                                                                                                                                                                                                                                    buildCondBr(builder)(buildICmp(builder)(intPredicateSgt)(spawnedTotal)(parConst(i64)(0))("parq_any_spawned"))(returnBlock)(drainInline)
                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(drainInline)
                                                                                                                                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                                                                                                                                        let _ = buildCall(builder)(drainType)(drainFn)([desc])(1u32)("")
                                                                                                                                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                                                                                                                                            let _ = buildBr(builder)(returnBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                                                                                                                                let _ = positionBuilderAtEnd(builder)(returnBlock)
                                                                                                                                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                                                                                                                                    let _ = buildRet(builder)(desc)
                                                                                                                                                                                                                                                                                                                                                                                                                                    in (startFn, startType))

// Stage 0's `__ashes_parallel_queue_await(queue)`: waits until the merge root (the last item) is
// published and answers it, still in the workers' arenas. Only called for a non-empty list.
let defineQueueAwaitFunction module_ context builder i64 i8 ptrType =
    (let awaitType = functionType(i64)([i64])(1u32)(false)
    in
        let awaitFn = addInternalFunction(module_)("__ashes_parallel_queue_await")(awaitType)
        in
            let _ =
                "entry"
                |> appendBasicBlock(context)(awaitFn)
                |> positionBuilderAtEnd(builder)
            in
                let desc = getParam(awaitFn)(0u32)
                in
                    let count = parWord(builder)(i64)(i8)(ptrType)(desc)(queueCount)("parq_await_n")
                    in
                        let itemBytes =
                            buildMul(builder)(parWord(builder)(i64)(i8)(ptrType)(desc)(queueTotalItems)("parq_await_s"))(parConst(i64)(8))("parq_await_item_bytes")
                        in
                            let itemsBase =
                                buildAdd(builder)(buildAdd(builder)(desc)(parConst(i64)(queueHeaderBytes))("parq_await_elems"))(buildMul(builder)(count)(parConst(i64)(8))("parq_await_n_bytes"))("parq_await_items")
                            in
                                let rootBytes =
                                    buildSub(builder)(itemBytes)(parConst(i64)(8))("parq_await_root_bytes")
                                in
                                    let _ =
                                        emitQueueFlagWait(context)(awaitFn)(builder)(i64)(buildAdd(builder)(buildAdd(builder)(itemsBase)(itemBytes)("parq_await_flags"))(rootBytes)("parq_await_root_flag"))("parq_await")
                                    in
                                        let _ =
                                            "parq_await_result"
                                            |> parWord(builder)(i64)(i8)(ptrType)(buildAdd(builder)(itemsBase)(rootBytes)("parq_await_root"))(0)
                                            |> buildRet(builder)
                                        in (awaitFn, awaitType))

// Stage 0's `__ashes_parallel_queue_cleanup(queue)`: releases every worker the queue started, then
// unmaps the queue region.
let defineQueueCleanupFunction module_ context builder i64 i8 ptrType =
    (let cleanupType =
        functionType(voidType(context))([i64])(1u32)(false)
    in
        let cleanupFn = addInternalFunction(module_)("__ashes_parallel_queue_cleanup")(cleanupType)
        in
            let loopBlock = appendBasicBlock(context)(cleanupFn)("parq_cleanup_loop")
            in
                let bodyBlock = appendBasicBlock(context)(cleanupFn)("parq_cleanup_body")
                in
                    let regionBlock = appendBasicBlock(context)(cleanupFn)("parq_cleanup_region")
                    in
                        let _ =
                            "entry"
                            |> appendBasicBlock(context)(cleanupFn)
                            |> positionBuilderAtEnd(builder)
                        in
                            let desc = getParam(cleanupFn)(0u32)
                            in
                                let count = parWord(builder)(i64)(i8)(ptrType)(desc)(queueCount)("parq_cleanup_n")
                                in
                                    let totalItems = parWord(builder)(i64)(i8)(ptrType)(desc)(queueTotalItems)("parq_cleanup_s")
                                    in
                                        let workers = parWord(builder)(i64)(i8)(ptrType)(desc)(queueWorkerCount)("parq_cleanup_workers")
                                        in
                                            let regionBytes = parWord(builder)(i64)(i8)(ptrType)(desc)(queueRegionBytes)("parq_cleanup_size")
                                            in
                                                let payloadBytes =
                                                    buildAdd(builder)(buildMul(builder)(count)(parConst(i64)(8))("parq_cleanup_element_bytes"))(buildMul(builder)(totalItems)(parConst(i64)(16))("parq_cleanup_tree_bytes"))("parq_cleanup_payload")
                                                in
                                                    let recordsBase =
                                                        buildAdd(builder)(buildAdd(builder)(desc)(parConst(i64)(queueHeaderBytes))("parq_cleanup_elems"))(payloadBytes)("parq_cleanup_records")
                                                    in
                                                        let workerSlot = buildAlloca(builder)(i64)("parq_cleanup_worker")
                                                        in
                                                            let _ =
                                                                buildStore(builder)(parConst(i64)(0))(workerSlot)
                                                            in
                                                                let _ = buildBr(builder)(loopBlock)
                                                                in
                                                                    let _ = positionBuilderAtEnd(builder)(loopBlock)
                                                                    in
                                                                        let worker = buildLoad(builder)(i64)(workerSlot)("parq_cleanup_index")
                                                                        in
                                                                            let _ =
                                                                                buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(worker)(workers)("parq_cleanup_more"))(bodyBlock)(regionBlock)
                                                                            in
                                                                                let _ = positionBuilderAtEnd(builder)(bodyBlock)
                                                                                in
                                                                                    let _ =
                                                                                        emitReleaseWorker(context)(cleanupFn)(builder)(i64)(i8)(ptrType)(buildAdd(builder)(recordsBase)(buildMul(builder)(worker)(parConst(i64)(queueRecordBytes))("parq_cleanup_offset"))("parq_cleanup_record"))("parq_cleanup_worker")
                                                                                    in
                                                                                        let _ =
                                                                                            buildStore(builder)(buildAdd(builder)(buildLoad(builder)(i64)(workerSlot)("parq_cleanup_reload"))(parConst(i64)(1))("parq_cleanup_next"))(workerSlot)
                                                                                        in
                                                                                            let _ = buildBr(builder)(loopBlock)
                                                                                            in
                                                                                                let _ = positionBuilderAtEnd(builder)(regionBlock)
                                                                                                in
                                                                                                    let _ = emitLinuxMunmap(builder)(i64)(desc)(regionBytes)
                                                                                                    in
                                                                                                        let _ = buildRetVoid(builder)
                                                                                                        in (cleanupFn, cleanupType))

// The parallel runtime of a program that uses `Ashes.Task.Parallel`; `threads` when it forks or
// starts a queue.
let defineParallelRuntime module_ context builder i64 i8 ptrType (arena: ArenaRuntime) closureFnType (threads: Bool) =
    (let overrideGlobal = parZeroGlobal(module_)(i64)("__ashes_parallel_override")
    in
        if threads
        then
            let activeCounter = parZeroGlobal(module_)(i64)("__ashes_parallel_active")
            in
                match (defineWorkerCapFunction(module_)(context)(builder)(i64)(i8)(ptrType), defineQueueDrainFunction(module_)(context)(builder)(i64)(i8)(ptrType)(closureFnType)) with
                    | ((capFn, capType), (drainFn, drainType)) ->
                        let queueWorkerFn = defineQueueWorkerFunction(module_)(context)(builder)(i64)(i8)(ptrType)(drainFn)(drainType)(activeCounter)
                        in
                            match (defineQueueStartFunction(module_)(context)(builder)(i64)(i8)(ptrType)(arena)(drainFn)(drainType)(queueWorkerFn)(activeCounter)(capFn)(capType)(overrideGlobal), defineQueueAwaitFunction(module_)(context)(builder)(i64)(i8)(ptrType), defineQueueCleanupFunction(module_)(context)(builder)(i64)(i8)(ptrType)) with
                                | ((startFn, startType), (awaitFn, awaitType), (cleanupFn, cleanupType)) ->
                                    ParallelRuntime(
                                        overrideGlobal = overrideGlobal,
                                        activeCounter = Some(activeCounter),
                                        capFn = capFn,
                                        capType = capType,
                                        workerFn = defineWorkerFunction(module_)(context)(builder)(i64)(i8)(ptrType)(arena)(closureFnType)(activeCounter),
                                        queueStartFn = startFn,
                                        queueStartType = startType,
                                        queueAwaitFn = awaitFn,
                                        queueAwaitType = awaitType,
                                        queueCleanupFn = cleanupFn,
                                        queueCleanupType = cleanupType
                                    )
        else
            ParallelRuntime(
                overrideGlobal = overrideGlobal,
                activeCounter = None,
                capFn = overrideGlobal,
                capType = i64,
                workerFn = overrideGlobal,
                queueStartFn = overrideGlobal,
                queueStartType = i64,
                queueAwaitFn = overrideGlobal,
                queueAwaitType = i64,
                queueCleanupFn = overrideGlobal,
                queueCleanupType = i64
            ))

let emitParallelQueueStart builder (runtime: ParallelRuntime) mapper combine list name = buildCall(builder)(runtime.queueStartType)(runtime.queueStartFn)([mapper, combine, list])(3u32)(name)

let emitParallelQueueAwait builder (runtime: ParallelRuntime) desc name = buildCall(builder)(runtime.queueAwaitType)(runtime.queueAwaitFn)([desc])(1u32)(name)

let emitParallelQueueCleanup builder (runtime: ParallelRuntime) desc = buildCall(builder)(runtime.queueCleanupType)(runtime.queueCleanupFn)([desc])(1u32)("")
