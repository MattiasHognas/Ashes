// Structured parallelism for `Ashes.Task.Parallel`, stage 0's `LlvmCodegenParallel.cs` for
// linux-x64. `ParallelFork` runs the right thunk of `both` on a worker thread while the caller runs
// the left one: the worker is a raw `clone` thread on its own mapped stack whose GS base is its own
// control block, so it bumps its own arena (`IrCodegen.Arena`'s thread slots); the reference-counted
// region is shared under the allocator lock. When the worker cap (the detected core count, narrowed
// by `withWorkers`) is reached, the thunk runs inline instead. `ParallelJoin` waits on the
// descriptor's done word with a futex and answers the worker's result, which still lives in the
// worker's arena; `ParallelCleanup` waits for the thread to exit and unmaps its stack, arena chunks
// and control block. The work-conserving queue (`ParallelQueue*`) is not ported.
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
// and the function each worker thread runs.
type ParallelRuntime =
    | overrideGlobal: LLVMValueRef
    | activeCounter: Maybe(LLVMValueRef)
    | capFn: LLVMValueRef
    | capType: LLVMTypeRef
    | workerFn: LLVMValueRef

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

// The parallel runtime of a program that uses `Ashes.Task.Parallel`; `threads` when it forks.
let defineParallelRuntime module_ context builder i64 i8 ptrType (arena: ArenaRuntime) closureFnType (threads: Bool) =
    (let overrideGlobal = parZeroGlobal(module_)(i64)("__ashes_parallel_override")
    in
        if threads
        then
            let activeCounter = parZeroGlobal(module_)(i64)("__ashes_parallel_active")
            in
                match defineWorkerCapFunction(module_)(context)(builder)(i64)(i8)(ptrType) with
                    | (capFn, capType) ->
                        ParallelRuntime(
                            overrideGlobal = overrideGlobal,
                            activeCounter = Some(activeCounter),
                            capFn = capFn,
                            capType = capType,
                            workerFn = defineWorkerFunction(module_)(context)(builder)(i64)(i8)(ptrType)(arena)(closureFnType)(activeCounter)
                        )
        else
            ParallelRuntime(
                overrideGlobal = overrideGlobal,
                activeCounter = None,
                capFn = overrideGlobal,
                capType = i64,
                workerFn = overrideGlobal
            ))

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
let emitEffectiveWorkerCap builder i64 (runtime: ParallelRuntime) =
    (let compiledMax = buildCall(builder)(runtime.capType)(runtime.capFn)([])(0u32)("par_cap_max")
    in
        let overrideValue = buildLoad(builder)(i64)(runtime.overrideGlobal)("par_cap_override")
        in
            let useOverride =
                buildAnd(builder)(buildICmp(builder)(intPredicateNe)(overrideValue)(parConst(i64)(0))("par_cap_override_set"))(buildICmp(builder)(intPredicateSlt)(overrideValue)(compiledMax)("par_cap_override_lower"))("par_cap_use_override")
            in buildSelect(builder)(useOverride)(overrideValue)(compiledMax)("par_cap"))

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
