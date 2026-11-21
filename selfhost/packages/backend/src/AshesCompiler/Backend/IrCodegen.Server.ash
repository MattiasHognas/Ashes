// Linux reactor state shared by the fork, stop, and accept-drain paths.
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Arena
import AshesCompiler.Backend.IrCodegen.Syscalls.LinuxX64
import AshesCompiler.Backend.IrCodegen.Parallel
export (
    type ServerRuntime(..),
    value defineServerRuntime,
    value defineForkWorkers,
    value emitRequestServerStop,
)

type ServerRuntime =
    | serverShutdown: LLVMValueRef
    | serverLiveSpawned: LLVMValueRef
    | serverDeadline: LLVMValueRef
    | serverTimeout: LLVMValueRef
    | serverChildCount: LLVMValueRef
    | serverChildPids: LLVMValueRef
    | serverIsWorker: LLVMValueRef

let defineServerRuntime module_ i64 =
    (let timeout = addGlobal(module_)(i64)("__ashes_drain_timeout_ms")
    in
        let _ =
            false
            |> constInt(i64)(10000u64)
            |> setInitializer(timeout)
        in
            let _ = setLinkage(timeout)(linkageInternal)
            in
                let pidsType = arrayType(i64)(1024u64)
                in
                    let pids = addGlobal(module_)(pidsType)("__ashes_child_pids")
                    in
                        let _ =
                            pidsType
                            |> constNull
                            |> setInitializer(pids)
                        in
                            let _ = setLinkage(pids)(linkageInternal)
                            in ServerRuntime(serverShutdown = addZeroWordGlobal(module_)(i64)("__ashes_shutdown_requested"), serverLiveSpawned = addZeroWordGlobal(module_)(i64)("__ashes_live_spawned"), serverDeadline = addZeroWordGlobal(module_)(i64)("__ashes_drain_deadline"), serverTimeout = timeout, serverChildCount = addZeroWordGlobal(module_)(i64)("__ashes_child_count"), serverChildPids = pids, serverIsWorker = addZeroWordGlobal(module_)(i64)("__ashes_is_worker")))

let constant i64 bits = constInt(i64)(bits)(false)

let defineSignalHandler module_ context builder i64 (server: ServerRuntime) =
    (let signature =
        functionType(voidType(context))([int32Type(context)])(1u32)(false)
    in
        let handler = addInternalFunction(module_)("__ashes_sig_handler")(signature)
        in
            let entry = appendBasicBlock(context)(handler)("entry")
            in
                let first = appendBasicBlock(context)(handler)("first_signal")
                in
                    let force = appendBasicBlock(context)(handler)("force_exit")
                    in
                        let _ = positionBuilderAtEnd(builder)(entry)
                        in
                            let previous = buildLoad(builder)(i64)(server.serverShutdown)("previous")
                            in
                                let _ = setVolatile(previous)(true)
                                in
                                    let _ =
                                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(previous)(constant(i64)(0u64))("first"))(first)(force)
                                    in
                                        let _ = positionBuilderAtEnd(builder)(first)
                                        in
                                            let _ =
                                                setVolatile(buildStore(builder)(constant(i64)(1u64))(server.serverShutdown))(true)
                                            in
                                                let _ = buildRetVoid(builder)
                                                in
                                                    let _ = positionBuilderAtEnd(builder)(force)
                                                    in
                                                        let _ =
                                                            0u64
                                                            |> constant(i64)
                                                            |> emitLinuxProcessExitWithCode(builder)(i64)
                                                        in handler)

let defineSignalRestorer module_ context builder =
    (let signature =
        functionType(voidType(context))([])(0u32)(false)
    in
        let restorer = addInternalFunction(module_)("__ashes_sig_restorer")(signature)
        in
            let _ = addFunctionEnumAttribute(context)(restorer)("naked")
            in
                let _ =
                    "entry"
                    |> appendBasicBlock(context)(restorer)
                    |> positionBuilderAtEnd(builder)
                in
                    let rtSigreturn = getInlineAsm(signature)("movq $$15, %rax\n\tsyscall")("~{rax},~{rcx},~{r11},~{memory},~{cc}")(true)(false)
                    in
                        let _ = buildCall(builder)(signature)(rtSigreturn)([])(0u32)("")
                        in
                            let _ = buildUnreachable(builder)
                            in restorer)

let installSignalHandlers builder i64 handler restorer =
    (let word = constant(i64)
    in
        let action =
            buildEntryAlloca(builder)(arrayType(i64)(4u64))("kernel_sigaction")
        in
            let slot =
                given (index) -> buildGEP(builder)(i64)(action)([word(index)])(1u32)("sigaction_slot")
            in
                let _ =
                    0u64
                    |> slot
                    |> buildStore(builder)(buildPtrToInt(builder)(handler)(i64)("handler_addr"))
                in
                    let _ =
                        1u64
                        |> slot
                        |> buildStore(builder)(word(67108864u64))
                    in
                        let _ =
                            2u64
                            |> slot
                            |> buildStore(builder)(buildPtrToInt(builder)(restorer)(i64)("restorer_addr"))
                        in
                            let _ =
                                3u64
                                |> slot
                                |> buildStore(builder)(word(0u64))
                            in
                                let address = buildPtrToInt(builder)(action)(i64)("action_addr")
                                in
                                    let _ =
                                        emitLinuxSyscallCall4(builder)(i64)(word(13u64))(word(2u64))(address)(word(0u64))(word(8u64))("install_sigint")
                                    in
                                        emitLinuxSyscallCall4(builder)(i64)(word(13u64))(word(15u64))(address)(word(0u64))(word(8u64))("install_sigterm"))

let emitRequestServerStop builder i64 (server: ServerRuntime) =
    (let word = constant(i64)
    in
        let zero = word(0u64)
        in
            let worker = buildLoad(builder)(i64)(server.serverIsWorker)("stop_worker")
            in
                let self =
                    emitLinuxSyscallCall(builder)(i64)(word(39u64))(zero)(zero)(zero)("stop_pid")
                in
                    let parent =
                        emitLinuxSyscallCall(builder)(i64)(word(110u64))(zero)(zero)(zero)("stop_ppid")
                    in
                        let target =
                            buildSelect(builder)(buildICmp(builder)(intPredicateNe)(worker)(zero)("is_worker"))(parent)(self)("stop_target")
                        in
                            let _ =
                                15u64
                                |> word
                                |> emitLinuxKill(builder)(i64)(target)
                            in zero)

let defineForkWorkers module_ context builder i64 (server: ServerRuntime) (parallel: ParallelRuntime) =
    (let handler = defineSignalHandler(module_)(context)(builder)(i64)(server)
    in
        let restorer = defineSignalRestorer(module_)(context)(builder)
        in
            let signature = functionType(i64)([i64])(1u32)(false)
            in
                let fn = addInternalFunction(module_)("__ashes_fork_workers")(signature)
                in
                    let block =
                        given (name) -> appendBasicBlock(context)(fn)(name)
                    in
                        let entry = block("entry")
                        in
                            let check = block("check")
                            in
                                let spawn = block("fork")
                                in
                                    let child = block("child")
                                    in
                                        let parent = block("parent")
                                        in
                                            let record = block("record_pid")
                                            in
                                                let advance = block("advance")
                                                in
                                                    let done = block("done")
                                                    in
                                                        let word = constant(i64)
                                                        in
                                                            let zero = word(0u64)
                                                            in
                                                                let one = word(1u64)
                                                                in
                                                                    let at = positionBuilderAtEnd(builder)
                                                                    in
                                                                        let _ = at(entry)
                                                                        in
                                                                            let _ = installSignalHandlers(builder)(i64)(handler)(restorer)
                                                                            in
                                                                                let requested = getParam(fn)(0u32)
                                                                                in
                                                                                    let automatic = emitEffectiveWorkerCap(builder)(i64)(parallel)
                                                                                    in
                                                                                        let count =
                                                                                            buildSelect(builder)(buildICmp(builder)(intPredicateSle)(requested)(zero)("auto_count"))(automatic)(requested)("worker_count")
                                                                                        in
                                                                                            let counter = buildEntryAlloca(builder)(i64)("worker_index")
                                                                                            in
                                                                                                let _ = buildStore(builder)(one)(counter)
                                                                                                in
                                                                                                    let _ = buildBr(builder)(check)
                                                                                                    in
                                                                                                        let _ = at(check)
                                                                                                        in
                                                                                                            let index = buildLoad(builder)(i64)(counter)("index")
                                                                                                            in
                                                                                                                let _ =
                                                                                                                    buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(index)(count)("more_workers"))(spawn)(done)
                                                                                                                in
                                                                                                                    let _ = at(spawn)
                                                                                                                    in
                                                                                                                        let pid = emitLinuxFork(builder)(i64)
                                                                                                                        in
                                                                                                                            let _ =
                                                                                                                                buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(pid)(zero)("is_child"))(child)(parent)
                                                                                                                            in
                                                                                                                                let _ = at(child)
                                                                                                                                in
                                                                                                                                    let _ =
                                                                                                                                        emitLinuxSyscallCall(builder)(i64)(word(157u64))(one)(word(15u64))(zero)("pr_set_pdeathsig")
                                                                                                                                    in
                                                                                                                                        let _ = buildStore(builder)(zero)(server.serverChildCount)
                                                                                                                                        in
                                                                                                                                            let _ = buildStore(builder)(one)(server.serverIsWorker)
                                                                                                                                            in
                                                                                                                                                let _ = buildRet(builder)(index)
                                                                                                                                                in
                                                                                                                                                    let _ = at(parent)
                                                                                                                                                    in
                                                                                                                                                        let used = buildLoad(builder)(i64)(server.serverChildCount)("child_count")
                                                                                                                                                        in
                                                                                                                                                            let positive = buildICmp(builder)(intPredicateSgt)(pid)(zero)("valid_pid")
                                                                                                                                                            in
                                                                                                                                                                let capacity =
                                                                                                                                                                    buildICmp(builder)(intPredicateSlt)(used)(word(1024u64))("has_slot")
                                                                                                                                                                in
                                                                                                                                                                    let _ =
                                                                                                                                                                        buildCondBr(builder)(buildAnd(builder)(positive)(capacity)("track_child"))(record)(advance)
                                                                                                                                                                    in
                                                                                                                                                                        let _ = at(record)
                                                                                                                                                                        in
                                                                                                                                                                            let slot = buildGEP(builder)(i64)(server.serverChildPids)([used])(1u32)("child_pid_slot")
                                                                                                                                                                            in
                                                                                                                                                                                let _ = buildStore(builder)(pid)(slot)
                                                                                                                                                                                in
                                                                                                                                                                                    let _ =
                                                                                                                                                                                        buildStore(builder)(buildAdd(builder)(used)(one)("next_pid_slot"))(server.serverChildCount)
                                                                                                                                                                                    in
                                                                                                                                                                                        let _ = buildBr(builder)(advance)
                                                                                                                                                                                        in
                                                                                                                                                                                            let _ = at(advance)
                                                                                                                                                                                            in
                                                                                                                                                                                                let _ =
                                                                                                                                                                                                    buildStore(builder)(buildAdd(builder)(index)(one)("next_worker"))(counter)
                                                                                                                                                                                                in
                                                                                                                                                                                                    let _ = buildCondBr(builder)(positive)(check)(done)
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let _ = at(done)
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let _ = buildRet(builder)(zero)
                                                                                                                                                                                                            in (fn, signature))
