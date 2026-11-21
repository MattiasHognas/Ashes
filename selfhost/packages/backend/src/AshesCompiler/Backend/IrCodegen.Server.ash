// Linux reactor state shared by the fork, stop, and accept-drain paths.
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Arena
export (
    type ServerRuntime(..),
    value defineServerRuntime,
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
