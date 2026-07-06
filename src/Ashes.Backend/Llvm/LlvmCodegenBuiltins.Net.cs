using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{

    private static void EmitNetworkingRuntimeAbi(
        LlvmTargetContext target,
        LlvmCodegenFlavor flavor,
        LlvmTypeHandle i32,
        LlvmTypeHandle i32Ptr,
        LlvmValueHandle heapCursorGlobal,
        LlvmValueHandle heapEndGlobal,
        LlvmValueHandle windowsGetStdHandleImport,
        LlvmValueHandle windowsWriteFileImport,
        LlvmValueHandle windowsReadFileImport,
        LlvmValueHandle windowsCreateFileImport,
        LlvmValueHandle windowsCloseHandleImport,
        LlvmValueHandle windowsGetFileAttributesImport,
        LlvmValueHandle windowsWsaStartupImport,
        LlvmValueHandle windowsSocketImport,
        LlvmValueHandle windowsConnectImport,
        LlvmValueHandle windowsSendImport,
        LlvmValueHandle windowsRecvImport,
        LlvmValueHandle windowsCloseSocketImport,
        LlvmValueHandle windowsIoctlSocketImport,
        LlvmValueHandle windowsWsaGetLastErrorImport,
        LlvmValueHandle windowsWsaPollImport,
        LlvmValueHandle windowsLoadLibraryImport,
        LlvmValueHandle windowsGetProcAddressImport,
        LlvmValueHandle windowsCertOpenSystemStoreImport,
        LlvmValueHandle windowsCertEnumCertificatesInStoreImport,
        LlvmValueHandle windowsCertCloseStoreImport,
        LlvmValueHandle windowsBindImport,
        LlvmValueHandle windowsSetSockOptImport,
        LlvmValueHandle windowsWsaIoctlImport,
        LlvmValueHandle windowsWsaSendImport,
        LlvmValueHandle windowsWsaRecvImport,
        LlvmValueHandle windowsCreateIoCompletionPortImport,
        LlvmValueHandle windowsGetQueuedCompletionStatusImport,
        LlvmValueHandle windowsIocpPortGlobal,
        LlvmValueHandle windowsExitProcessImport,
        LlvmValueHandle windowsGetCommandLineImport,
        LlvmValueHandle windowsWideCharToMultiByteImport,
        LlvmValueHandle windowsLocalFreeImport,
        LlvmValueHandle windowsCommandLineToArgvImport,
        LlvmValueHandle windowsSleepImport,
        LlvmValueHandle windowsVirtualAllocImport,
        LlvmValueHandle windowsVirtualFreeImport,
        HermeticTlsRuntimeAsset? rustlsSharedLibrary,
        LlvmAttributeHandle nounwindAttr)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmTypeHandle i8 = LlvmApi.Int8TypeInContext(target.Context);
        LlvmTypeHandle f64 = LlvmApi.DoubleTypeInContext(target.Context);
        LlvmTypeHandle i8Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
        LlvmTypeHandle i64Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
        LlvmValueHandle rustlsReadCallback = default;
        LlvmValueHandle rustlsWriteCallback = default;
        if (IsLinuxFlavor(flavor) || flavor == LlvmCodegenFlavor.WindowsX64)
        {
            LlvmTypeHandle rustlsIoCallbackType = LlvmApi.FunctionType(i32, [i8Ptr, i8Ptr, i64, i64Ptr]);
            rustlsReadCallback = EmitInternalRuntimeFunction(
                "__ashes_tls_rustls_socket_read_callback",
                rustlsIoCallbackType,
                (state, fn) =>
                {
                    LlvmBuilderHandle builder = state.Target.Builder;
                    LlvmValueHandle socket = LlvmApi.BuildPtrToInt(builder, LlvmApi.GetParam(fn, 0), state.I64, "tls_rustls_read_socket");
                    LlvmValueHandle bufferPtr = LlvmApi.BuildPtrToInt(builder, LlvmApi.GetParam(fn, 1), state.I64, "tls_rustls_read_buffer_ptr");
                    LlvmValueHandle requestedBytes = LlvmApi.GetParam(fn, 2);
                    LlvmValueHandle outBytesSlot = LlvmApi.GetParam(fn, 3);
                    LlvmValueHandle readCount = IsLinuxFlavor(state.Flavor)
                        ? EmitLinuxSyscall(state, SyscallRead, socket, bufferPtr, requestedBytes, "tls_rustls_read_syscall")
                        : LlvmApi.BuildSExt(builder,
                            EmitWindowsRecv(state, socket, LlvmApi.GetParam(fn, 1), LlvmApi.BuildTrunc(builder, requestedBytes, state.I32, "tls_rustls_read_requested_i32"), "tls_rustls_read_recv"),
                            state.I64,
                            "tls_rustls_read_count");
                    LlvmValueHandle readOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, readCount, LlvmApi.ConstInt(state.I64, 0, 0), "tls_rustls_read_ok");

                    LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tls_rustls_read_success");
                    LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tls_rustls_read_fail");
                    LlvmApi.BuildCondBr(builder, readOk, successBlock, failBlock);

                    LlvmApi.PositionBuilderAtEnd(builder, successBlock);
                    LlvmApi.BuildStore(builder, readCount, outBytesSlot);
                    LlvmApi.BuildRet(builder, LlvmApi.ConstInt(state.I32, 0, 0));

                    LlvmApi.PositionBuilderAtEnd(builder, failBlock);
                    LlvmValueHandle errorCode = IsLinuxFlavor(state.Flavor)
                        ? LlvmApi.BuildTrunc(builder,
                            LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 0, 0), readCount, "tls_rustls_read_errno_i64"),
                            state.I32,
                            "tls_rustls_read_errno")
                        : EmitWindowsWsaGetLastError(state, "tls_rustls_read_wsa_error");
                    LlvmApi.BuildRet(builder, errorCode);
                    return LlvmApi.ConstInt(state.I32, 0, 0);
                });
            rustlsWriteCallback = EmitInternalRuntimeFunction(
                "__ashes_tls_rustls_socket_write_callback",
                rustlsIoCallbackType,
                (state, fn) =>
                {
                    LlvmBuilderHandle builder = state.Target.Builder;
                    LlvmValueHandle socket = LlvmApi.BuildPtrToInt(builder, LlvmApi.GetParam(fn, 0), state.I64, "tls_rustls_write_socket");
                    LlvmValueHandle bufferPtr = LlvmApi.BuildPtrToInt(builder, LlvmApi.GetParam(fn, 1), state.I64, "tls_rustls_write_buffer_ptr");
                    LlvmValueHandle requestedBytes = LlvmApi.GetParam(fn, 2);
                    LlvmValueHandle outBytesSlot = LlvmApi.GetParam(fn, 3);
                    LlvmValueHandle writeCount = IsLinuxFlavor(state.Flavor)
                        ? EmitLinuxSyscall(state, SyscallWrite, socket, bufferPtr, requestedBytes, "tls_rustls_write_syscall")
                        : LlvmApi.BuildSExt(builder,
                            EmitWindowsSend(state, socket, LlvmApi.GetParam(fn, 1), LlvmApi.BuildTrunc(builder, requestedBytes, state.I32, "tls_rustls_write_requested_i32"), "tls_rustls_write_send"),
                            state.I64,
                            "tls_rustls_write_count");
                    LlvmValueHandle writeOk = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, writeCount, LlvmApi.ConstInt(state.I64, 0, 0), "tls_rustls_write_ok");

                    LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tls_rustls_write_success");
                    LlvmBasicBlockHandle failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tls_rustls_write_fail");
                    LlvmApi.BuildCondBr(builder, writeOk, successBlock, failBlock);

                    LlvmApi.PositionBuilderAtEnd(builder, successBlock);
                    LlvmApi.BuildStore(builder, writeCount, outBytesSlot);
                    LlvmApi.BuildRet(builder, LlvmApi.ConstInt(state.I32, 0, 0));

                    LlvmApi.PositionBuilderAtEnd(builder, failBlock);
                    LlvmValueHandle errorCode = IsLinuxFlavor(state.Flavor)
                        ? LlvmApi.BuildTrunc(builder,
                            LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 0, 0), writeCount, "tls_rustls_write_errno_i64"),
                            state.I32,
                            "tls_rustls_write_errno")
                        : EmitWindowsWsaGetLastError(state, "tls_rustls_write_wsa_error");
                    LlvmApi.BuildRet(builder, errorCode);
                    return LlvmApi.ConstInt(state.I32, 0, 0);
                });
        }

        LinuxTlsGlobals linuxTlsGlobals = IsLinuxFlavor(flavor) || flavor == LlvmCodegenFlavor.WindowsX64
            ? new LinuxTlsGlobals(
                CreateInternalI64Global("__ashes_tls_init_status"),
                CreateInternalI64Global("__ashes_tls_ctx"),
                CreateInternalI64Global("__ashes_tls_libssl_handle"),
                rustlsReadCallback,
                rustlsWriteCallback,
                CreateInternalI64Global("__ashes_tls_server_config"))
            : default;
        LlvmValueHandle linkedTlsPayloadStartGlobal = default;
        LlvmValueHandle linkedTlsPayloadEndGlobal = default;
        if (rustlsSharedLibrary is not null)
        {
            linkedTlsPayloadStartGlobal = LlvmApi.AddGlobal(target.Module, i8, HermeticTlsLinkPayloadSymbols.StartSymbolName);
            LlvmApi.SetLinkage(linkedTlsPayloadStartGlobal, LlvmLinkage.External);
            linkedTlsPayloadEndGlobal = LlvmApi.AddGlobal(target.Module, i8, HermeticTlsLinkPayloadSymbols.EndSymbolName);
            LlvmApi.SetLinkage(linkedTlsPayloadEndGlobal, LlvmLinkage.External);
        }

        DeclareRuntimeFunction("ashes_tcp_connect", LlvmApi.FunctionType(i64, [i64, i64]));
        DeclareRuntimeFunction("ashes_tcp_send", LlvmApi.FunctionType(i64, [i64, i64]));
        DeclareRuntimeFunction("ashes_tcp_receive", LlvmApi.FunctionType(i64, [i64, i64]));
        DeclareRuntimeFunction("ashes_tcp_close", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_http_get", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_http_post", LlvmApi.FunctionType(i64, [i64, i64]));
        DeclareRuntimeFunction("ashes_tls_runtime_init", LlvmApi.FunctionType(i64, []));
        DeclareRuntimeFunction("ashes_step_tcp_connect_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tcp_send_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tcp_receive_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tcp_close_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tcp_listen_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tcp_accept_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tls_connect_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tls_handshake_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tls_server_handshake_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tls_send_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tls_receive_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_tls_close_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_task_until_wait_or_done", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_wait_pending_task_list", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_run_detached", LlvmApi.FunctionType(i64, []));
        DeclareRuntimeFunction("ashes_detached_wait_meta", LlvmApi.FunctionType(i64, []));
        DeclareRuntimeFunction("ashes_detached_advance_timers", LlvmApi.FunctionType(i64, [i64]));
        if (flavor == LlvmCodegenFlavor.WindowsX64)
        {
            DeclareRuntimeFunction("ashes_detached_fill_pollfds", LlvmApi.FunctionType(i64, [i64, i64]));
        }
        else
        {
            DeclareRuntimeFunction("ashes_detached_register_epoll", LlvmApi.FunctionType(i64, [i64]));
            DeclareRuntimeFunction("ashes_epoll_register", LlvmApi.FunctionType(i64, [i64, i64, i64]));
            DeclareRuntimeFunction("ashes_epoll_forget", LlvmApi.FunctionType(i64, [i64]));
        }
        DeclareRuntimeFunction("ashes_step_http_get_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_step_http_post_task", LlvmApi.FunctionType(i64, [i64]));
        DeclareRuntimeFunction("ashes_cancel_task", LlvmApi.FunctionType(i64, [i64]));

        EmitRuntimeFunction(
            "ashes_tcp_connect",
            LlvmApi.FunctionType(i64, [i64, i64]),
            (state, fn) => EmitTcpConnect(state, LlvmApi.GetParam(fn, 0), LlvmApi.GetParam(fn, 1)));

        EmitRuntimeFunction(
            "ashes_tcp_send",
            LlvmApi.FunctionType(i64, [i64, i64]),
            (state, fn) => EmitTcpSend(state, LlvmApi.GetParam(fn, 0), LlvmApi.GetParam(fn, 1)));

        EmitRuntimeFunction(
            "ashes_tcp_receive",
            LlvmApi.FunctionType(i64, [i64, i64]),
            (state, fn) => EmitTcpReceive(state, LlvmApi.GetParam(fn, 0), LlvmApi.GetParam(fn, 1)));

        EmitRuntimeFunction(
            "ashes_tcp_close",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitTcpClose(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_http_get",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitHttpRequest(state, LlvmApi.GetParam(fn, 0), LlvmApi.ConstInt(state.I64, 0, 0), hasBody: false));

        EmitRuntimeFunction(
            "ashes_http_post",
            LlvmApi.FunctionType(i64, [i64, i64]),
            (state, fn) => EmitHttpRequest(state, LlvmApi.GetParam(fn, 0), LlvmApi.GetParam(fn, 1), hasBody: true));

        EmitRuntimeFunction(
            "ashes_tls_runtime_init",
            LlvmApi.FunctionType(i64, []),
            (state, _) => IsLinuxFlavor(state.Flavor)
                ? EmitEnsureLinuxTlsRuntimeInitialized(state, linuxTlsGlobals, rustlsSharedLibrary, linkedTlsPayloadStartGlobal, linkedTlsPayloadEndGlobal, "tls_runtime_init")
                : state.Flavor == LlvmCodegenFlavor.WindowsX64
                    ? EmitEnsureWindowsTlsRuntimeInitialized(state, linuxTlsGlobals, rustlsSharedLibrary, linkedTlsPayloadStartGlobal, linkedTlsPayloadEndGlobal, "tls_runtime_init")
                : LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1));

        EmitRuntimeFunction(
            "ashes_step_tcp_connect_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTcpConnectTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_tcp_send_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTcpSendTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_tcp_receive_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTcpReceiveTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_tcp_close_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTcpCloseTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_tcp_listen_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTcpListenTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_tcp_accept_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTcpAcceptTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_fork_workers_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepForkWorkersTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_tls_connect_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTlsConnectTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_tls_handshake_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => IsLinuxFlavor(state.Flavor) || state.Flavor == LlvmCodegenFlavor.WindowsX64
                ? EmitStepTlsHandshakeTask(state, LlvmApi.GetParam(fn, 0), linuxTlsGlobals)
                : EmitStepTlsTodoTask(state, LlvmApi.GetParam(fn, 0), "Ashes TLS handshake runtime is not implemented yet", "step_tls_handshake_todo"));

        EmitRuntimeFunction(
            "ashes_step_tls_server_handshake_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => IsLinuxFlavor(state.Flavor) || state.Flavor == LlvmCodegenFlavor.WindowsX64
                ? EmitStepTlsHandshakeTask(state, LlvmApi.GetParam(fn, 0), linuxTlsGlobals, serverSide: true)
                : EmitStepTlsTodoTask(state, LlvmApi.GetParam(fn, 0), "Ashes server TLS handshake runtime is not implemented yet", "step_tls_server_handshake_todo"));

        EmitRuntimeFunction(
            "ashes_step_tls_send_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => IsLinuxFlavor(state.Flavor) || state.Flavor == LlvmCodegenFlavor.WindowsX64
                ? EmitStepTlsSendTask(state, LlvmApi.GetParam(fn, 0), linuxTlsGlobals)
                : EmitStepTlsTodoTask(state, LlvmApi.GetParam(fn, 0), "Ashes TLS send runtime is not implemented yet", "step_tls_send_todo"));

        EmitRuntimeFunction(
            "ashes_step_tls_receive_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => IsLinuxFlavor(state.Flavor) || state.Flavor == LlvmCodegenFlavor.WindowsX64
                ? EmitStepTlsReceiveTask(state, LlvmApi.GetParam(fn, 0), linuxTlsGlobals)
                : EmitStepTlsTodoTask(state, LlvmApi.GetParam(fn, 0), "Ashes TLS receive runtime is not implemented yet", "step_tls_receive_todo"));

        EmitRuntimeFunction(
            "ashes_step_tls_close_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => IsLinuxFlavor(state.Flavor) || state.Flavor == LlvmCodegenFlavor.WindowsX64
                ? EmitStepTlsCloseTask(state, LlvmApi.GetParam(fn, 0), linuxTlsGlobals)
                : EmitStepTlsTodoTask(state, LlvmApi.GetParam(fn, 0), "Ashes TLS close runtime is not implemented yet", "step_tls_close_todo"));

        EmitRuntimeFunction(
            "ashes_step_task_until_wait_or_done",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepTaskUntilPendingOrDone(state, LlvmApi.GetParam(fn, 0), "runtime_step_task"));

        EmitRuntimeFunction(
            "ashes_wait_pending_task_list",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitWaitForPendingTaskList(state, LlvmApi.GetParam(fn, 0), "runtime_wait_tasks"));

        EmitRuntimeFunction(
            "ashes_run_detached",
            LlvmApi.FunctionType(i64, []),
            (state, fn) => EmitRunDetachedBody(state));

        EmitRuntimeFunction(
            "ashes_detached_wait_meta",
            LlvmApi.FunctionType(i64, []),
            (state, fn) => EmitDetachedWaitMetaBody(state));

        EmitRuntimeFunction(
            "ashes_detached_advance_timers",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitDetachedAdvanceTimersBody(state, LlvmApi.GetParam(fn, 0)));

        if (flavor == LlvmCodegenFlavor.WindowsX64)
        {
            EmitRuntimeFunction(
                "ashes_detached_fill_pollfds",
                LlvmApi.FunctionType(i64, [i64, i64]),
                (state, fn) => EmitDetachedFillPollFdsBody(state, LlvmApi.GetParam(fn, 0), LlvmApi.GetParam(fn, 1)));
        }
        else
        {
            EmitRuntimeFunction(
                "ashes_detached_register_epoll",
                LlvmApi.FunctionType(i64, [i64]),
                (state, fn) => EmitDetachedRegisterEpollBody(state, LlvmApi.GetParam(fn, 0)));
            EmitRuntimeFunction(
                "ashes_epoll_register",
                LlvmApi.FunctionType(i64, [i64, i64, i64]),
                (state, fn) => EmitEpollRegisterBody(state, LlvmApi.GetParam(fn, 0), LlvmApi.GetParam(fn, 1), LlvmApi.GetParam(fn, 2)));
            EmitRuntimeFunction(
                "ashes_epoll_forget",
                LlvmApi.FunctionType(i64, [i64]),
                (state, fn) => EmitEpollForgetBody(state, LlvmApi.GetParam(fn, 0)));
        }

        EmitRuntimeFunction(
            "ashes_step_http_get_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepHttpGetTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_step_http_post_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitStepHttpPostTask(state, LlvmApi.GetParam(fn, 0)));

        EmitRuntimeFunction(
            "ashes_cancel_task",
            LlvmApi.FunctionType(i64, [i64]),
            (state, fn) => EmitCancelTask(state, LlvmApi.GetParam(fn, 0)));

        LlvmValueHandle CreateInternalI64Global(string symbolName)
        {
            LlvmValueHandle global = LlvmApi.AddGlobal(target.Module, i64, symbolName);
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            LlvmApi.SetInitializer(global, LlvmApi.ConstInt(i64, 0, 0));
            return global;
        }

        void EmitRuntimeFunction(string symbolName, LlvmTypeHandle functionType, Func<LlvmCodegenState, LlvmValueHandle, LlvmValueHandle> emitBody)
        {
            LlvmValueHandle function = LlvmApi.GetNamedFunction(target.Module, symbolName);
            if (function.Ptr == 0)
            {
                function = LlvmApi.AddFunction(target.Module, symbolName, functionType);
            }
            LlvmApi.SetLinkage(function, LlvmLinkage.External);
            LlvmApi.AddAttributeAtIndex(function, LlvmApi.AttributeIndexFunction, nounwindAttr);

            LlvmBasicBlockHandle entryBlock = LlvmApi.AppendBasicBlockInContext(target.Context, function, "entry");
            LlvmApi.PositionBuilderAtEnd(target.Builder, entryBlock);

            LlvmValueHandle programArgsSlot = LlvmApi.BuildAlloca(target.Builder, i64, symbolName + "_program_args");
            LlvmApi.BuildStore(target.Builder, LlvmApi.ConstInt(i64, 0, 0), programArgsSlot);

            var runtimeState = new LlvmCodegenState(
                target,
                function,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, LlvmValueHandle>(StringComparer.Ordinal),
                programArgsSlot,
                Array.Empty<LlvmValueHandle>(),
                Array.Empty<LlvmValueHandle>(),
                heapCursorGlobal,
                heapEndGlobal,
                new Dictionary<string, LlvmBasicBlockHandle>(StringComparer.Ordinal),
                new Dictionary<int, LlvmBasicBlockHandle>(),
                i64,
                i32,
                i8,
                f64,
                i8Ptr,
                i32Ptr,
                i64Ptr,
                default,
                windowsGetStdHandleImport,
                windowsWriteFileImport,
                windowsReadFileImport,
                windowsCreateFileImport,
                windowsCloseHandleImport,
                windowsGetFileAttributesImport,
                windowsWsaStartupImport,
                windowsSocketImport,
                windowsConnectImport,
                windowsSendImport,
                windowsRecvImport,
                windowsCloseSocketImport,
                windowsIoctlSocketImport,
                windowsWsaGetLastErrorImport,
                windowsWsaPollImport,
                windowsLoadLibraryImport,
                windowsGetProcAddressImport,
                windowsCertOpenSystemStoreImport,
                windowsCertEnumCertificatesInStoreImport,
                windowsCertCloseStoreImport,
                windowsBindImport,
                windowsSetSockOptImport,
                windowsWsaIoctlImport,
                windowsWsaSendImport,
                windowsWsaRecvImport,
                windowsCreateIoCompletionPortImport,
                windowsGetQueuedCompletionStatusImport,
                windowsIocpPortGlobal,
                windowsExitProcessImport,
                windowsGetCommandLineImport,
                windowsWideCharToMultiByteImport,
                windowsLocalFreeImport,
                windowsCommandLineToArgvImport,
                windowsSleepImport,
                windowsVirtualAllocImport,
                windowsVirtualFreeImport,
                default,
                default,
                default,
                default,
                default,
                new Dictionary<string, LlvmValueHandle>(StringComparer.Ordinal),
                flavor,
                false,
                false);

            runtimeState = WithLinuxThreadArena(runtimeState);
            LlvmApi.BuildRet(target.Builder, NormalizeToI64(runtimeState, emitBody(runtimeState, function)));
        }

        LlvmValueHandle EmitInternalRuntimeFunction(string symbolName, LlvmTypeHandle functionType, Func<LlvmCodegenState, LlvmValueHandle, LlvmValueHandle> emitBody)
        {
            LlvmValueHandle function = LlvmApi.GetNamedFunction(target.Module, symbolName);
            if (function.Ptr == 0)
            {
                function = LlvmApi.AddFunction(target.Module, symbolName, functionType);
            }

            LlvmApi.SetLinkage(function, LlvmLinkage.Internal);
            LlvmApi.AddAttributeAtIndex(function, LlvmApi.AttributeIndexFunction, nounwindAttr);

            LlvmBasicBlockHandle entryBlock = LlvmApi.AppendBasicBlockInContext(target.Context, function, "entry");
            LlvmApi.PositionBuilderAtEnd(target.Builder, entryBlock);

            LlvmValueHandle programArgsSlot = LlvmApi.BuildAlloca(target.Builder, i64, symbolName + "_program_args");
            LlvmApi.BuildStore(target.Builder, LlvmApi.ConstInt(i64, 0, 0), programArgsSlot);

            var runtimeState = new LlvmCodegenState(
                target,
                function,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, LlvmValueHandle>(StringComparer.Ordinal),
                programArgsSlot,
                Array.Empty<LlvmValueHandle>(),
                Array.Empty<LlvmValueHandle>(),
                heapCursorGlobal,
                heapEndGlobal,
                new Dictionary<string, LlvmBasicBlockHandle>(StringComparer.Ordinal),
                new Dictionary<int, LlvmBasicBlockHandle>(),
                i64,
                i32,
                i8,
                f64,
                i8Ptr,
                i32Ptr,
                i64Ptr,
                default,
                windowsGetStdHandleImport,
                windowsWriteFileImport,
                windowsReadFileImport,
                windowsCreateFileImport,
                windowsCloseHandleImport,
                windowsGetFileAttributesImport,
                windowsWsaStartupImport,
                windowsSocketImport,
                windowsConnectImport,
                windowsSendImport,
                windowsRecvImport,
                windowsCloseSocketImport,
                windowsIoctlSocketImport,
                windowsWsaGetLastErrorImport,
                windowsWsaPollImport,
                windowsLoadLibraryImport,
                windowsGetProcAddressImport,
                windowsCertOpenSystemStoreImport,
                windowsCertEnumCertificatesInStoreImport,
                windowsCertCloseStoreImport,
                windowsBindImport,
                windowsSetSockOptImport,
                windowsWsaIoctlImport,
                windowsWsaSendImport,
                windowsWsaRecvImport,
                windowsCreateIoCompletionPortImport,
                windowsGetQueuedCompletionStatusImport,
                windowsIocpPortGlobal,
                windowsExitProcessImport,
                windowsGetCommandLineImport,
                windowsWideCharToMultiByteImport,
                windowsLocalFreeImport,
                windowsCommandLineToArgvImport,
                windowsSleepImport,
                windowsVirtualAllocImport,
                windowsVirtualFreeImport,
                default,
                default,
                default,
                default,
                default,
                new Dictionary<string, LlvmValueHandle>(StringComparer.Ordinal),
                flavor,
                false,
                false);

            runtimeState = WithLinuxThreadArena(runtimeState);
            emitBody(runtimeState, function);
            return function;
        }

        void DeclareRuntimeFunction(string symbolName, LlvmTypeHandle functionType)
        {
            if (LlvmApi.GetNamedFunction(target.Module, symbolName).Ptr == 0)
            {
                LlvmApi.AddFunction(target.Module, symbolName, functionType);
            }
        }
    }

    private static LlvmValueHandle EmitHttpGetAbiCall(LlvmCodegenState state, LlvmValueHandle urlRef)
        => EmitNetworkingRuntimeCall(state, "ashes_http_get", [urlRef], "http_get_abi");

    private static LlvmValueHandle EmitHttpPostAbiCall(LlvmCodegenState state, LlvmValueHandle urlRef, LlvmValueHandle bodyRef)
        => EmitNetworkingRuntimeCall(state, "ashes_http_post", [urlRef, bodyRef], "http_post_abi");

    private static LlvmValueHandle EmitTcpConnectAbiCall(LlvmCodegenState state, LlvmValueHandle hostRef, LlvmValueHandle port)
        => EmitNetworkingRuntimeCall(state, "ashes_tcp_connect", [hostRef, port], "tcp_connect_abi");

    private static LlvmValueHandle EmitTcpSendAbiCall(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle textRef)
        => EmitNetworkingRuntimeCall(state, "ashes_tcp_send", [socket, textRef], "tcp_send_abi");

    private static LlvmValueHandle EmitTcpReceiveAbiCall(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle maxBytes)
        => EmitNetworkingRuntimeCall(state, "ashes_tcp_receive", [socket, maxBytes], "tcp_receive_abi");

    private static LlvmValueHandle EmitTcpCloseAbiCall(LlvmCodegenState state, LlvmValueHandle socket)
        => EmitNetworkingRuntimeCall(state, "ashes_tcp_close", [socket], "tcp_close_abi");

    private static LlvmValueHandle EmitTcpListenAbiCall(LlvmCodegenState state, LlvmValueHandle port)
        => EmitRunTask(
            state,
            EmitCreateLeafNetworkingTask(
                state,
                TaskStructLayout.StateTcpListen,
                port,
                LlvmApi.ConstInt(state.I64, 0, 0),
                "tcp_listen_abi_task"));

    private static LlvmValueHandle EmitTcpAcceptAbiCall(LlvmCodegenState state, LlvmValueHandle socket)
        => EmitRunTask(
            state,
            EmitCreateLeafNetworkingTask(
                state,
                TaskStructLayout.StateTcpAccept,
                socket,
                LlvmApi.ConstInt(state.I64, 0, 0),
                "tcp_accept_abi_task"));

    private static LlvmValueHandle EmitTlsCloseAbiCall(LlvmCodegenState state, LlvmValueHandle session)
        => EmitRunTask(
            state,
            EmitCreateLeafNetworkingTask(
                state,
                TaskStructLayout.StateTlsClose,
                session,
                LlvmApi.ConstInt(state.I64, 0, 0),
                "tls_close_abi_task"));

    private static LlvmValueHandle EmitNetworkingRuntimeCall(LlvmCodegenState state, string symbolName, ReadOnlySpan<LlvmValueHandle> args, string name)
    {
        LlvmValueHandle function = LlvmApi.GetNamedFunction(state.Target.Module, symbolName);
        if (function.Ptr == 0)
        {
            throw new InvalidOperationException($"Missing networking runtime symbol '{symbolName}'.");
        }

        var parameterTypes = new LlvmTypeHandle[args.Length];
        Array.Fill(parameterTypes, state.I64);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I64, parameterTypes);
        return LlvmApi.BuildCall2(state.Target.Builder, functionType, function, args, name);
    }

    private static LlvmValueHandle EmitOrDeclareExternalFunction(LlvmCodegenState state, string symbolName, LlvmTypeHandle functionType)
    {
        LlvmValueHandle function = LlvmApi.GetNamedFunction(state.Target.Module, symbolName);
        if (function.Ptr == 0)
        {
            function = LlvmApi.AddFunction(state.Target.Module, symbolName, functionType);
            LlvmApi.SetLinkage(function, LlvmLinkage.External);
        }

        return function;
    }

    private static LlvmValueHandle EmitLinuxImportedCall(LlvmCodegenState state, string symbolName, LlvmTypeHandle functionType, ReadOnlySpan<LlvmValueHandle> args, string name)
    {
        LlvmValueHandle function = EmitOrDeclareExternalFunction(state, symbolName, functionType);
        return LlvmApi.BuildCall2(state.Target.Builder, functionType, function, args, name);
    }

    private static LlvmValueHandle EmitLinuxDlopen(LlvmCodegenState state, LlvmValueHandle pathCstr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I32]);
        LlvmValueHandle handlePtr = EmitLinuxImportedCall(
            state,
            "dlopen",
            functionType,
            [pathCstr, LlvmApi.ConstInt(state.I32, LinuxRtldNow, 0)],
            name);
        return LlvmApi.BuildPtrToInt(builder, handlePtr, state.I64, name + "_i64");
    }

    private static LlvmValueHandle EmitLinuxDlsym(LlvmCodegenState state, LlvmValueHandle libraryHandle, string symbolName, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle libraryPtr = LlvmApi.BuildIntToPtr(builder, libraryHandle, state.I8Ptr, name + "_library_ptr");
        LlvmValueHandle symbolCstr = EmitStringToCString(state, EmitHeapStringLiteral(state, symbolName), name + "_symbol_name");
        LlvmValueHandle symbolPtr = EmitLinuxImportedCall(state, "dlsym", functionType, [libraryPtr, symbolCstr], name + "_call");
        return LlvmApi.BuildPtrToInt(builder, symbolPtr, state.I64, name + "_i64");
    }

    private static LlvmValueHandle EmitLinuxStrlen(LlvmCodegenState state, LlvmValueHandle cstrPtr, string name)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I64, [state.I8Ptr]);
        return EmitLinuxImportedCall(state, "strlen", functionType, [cstrPtr], name);
    }

    private static LlvmValueHandle EmitLinuxGetEnv(LlvmCodegenState state, string variableName, string name)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmValueHandle variableNameCstr = EmitStringToCString(state, EmitHeapStringLiteral(state, variableName), name + "_variable_name");
        return EmitLinuxImportedCall(state, "getenv", functionType, [variableNameCstr], name);
    }

    private static LlvmValueHandle EmitLinuxGetPid(LlvmCodegenState state, string name)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, []);
        return EmitLinuxImportedCall(state, "getpid", functionType, Array.Empty<LlvmValueHandle>(), name);
    }

    private static LlvmValueHandle EmitWindowsLoadLibraryWithFallback(LlvmCodegenState state, string primaryName, string fallbackName, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle librarySlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_library_slot");
        LlvmBasicBlockHandle fallbackBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fallback");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");

        LlvmValueHandle primaryHandle = EmitWindowsLoadLibrary(state,
            EmitStringToCString(state, EmitHeapStringLiteral(state, primaryName), prefix + "_primary_name"),
            prefix + "_primary");
        LlvmApi.BuildStore(builder, primaryHandle, librarySlot);
        LlvmValueHandle hasPrimary = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, primaryHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_primary");
        LlvmApi.BuildCondBr(builder, hasPrimary, doneBlock, fallbackBlock);

        LlvmApi.PositionBuilderAtEnd(builder, fallbackBlock);
        LlvmValueHandle fallbackHandle = EmitWindowsLoadLibrary(state,
            EmitStringToCString(state, EmitHeapStringLiteral(state, fallbackName), prefix + "_fallback_name"),
            prefix + "_fallback");
        LlvmApi.BuildStore(builder, fallbackHandle, librarySlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, librarySlot, prefix + "_library");
    }

    private static LlvmValueHandle EmitTlsResolveSymbol(LlvmCodegenState state, LlvmValueHandle libraryHandle, string symbolName, string name)
    {
        return state.Flavor == LlvmCodegenFlavor.WindowsX64
            ? EmitWindowsGetProcAddress(state,
                libraryHandle,
                EmitStringToCString(state, EmitHeapStringLiteral(state, symbolName), name + "_symbol_name"),
                name + "_getproc")
            : EmitLinuxDlsym(state, libraryHandle, symbolName, name + "_dlsym");
    }

    private static void EmitSetSocketNonBlocking(LlvmCodegenState state, LlvmValueHandle socket, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        if (IsLinuxFlavor(state.Flavor))
        {
            LlvmValueHandle currentFlags = EmitLinuxSyscall(
                state,
                SyscallFcntl,
                socket,
                LlvmApi.ConstInt(state.I64, LinuxFcntlGetFlags, 0),
                LlvmApi.ConstInt(state.I64, 0, 0),
                prefix + "_getfl");
            LlvmValueHandle nextFlags = LlvmApi.BuildOr(builder,
                currentFlags,
                LlvmApi.ConstInt(state.I64, LinuxOpenNonBlocking, 0),
                prefix + "_flags_with_nonblock");
            EmitLinuxSyscall(
                state,
                SyscallFcntl,
                socket,
                LlvmApi.ConstInt(state.I64, LinuxFcntlSetFlags, 0),
                nextFlags,
                prefix + "_setfl");
        }
        else
        {
            LlvmValueHandle nonBlockingSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_nonblocking_slot");
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), nonBlockingSlot);
            EmitWindowsIoctlSocket(
                state,
                socket,
                LlvmApi.ConstInt(state.I64, WindowsFionBio, 0),
                nonBlockingSlot,
                prefix + "_ioctlsocket");
        }
    }

    /// <summary>
    /// Windows fork-based multi-reactor. Windows has neither fork nor SO_REUSEPORT, so workers are
    /// separate processes relaunched from the same executable that share a single inheritable listener
    /// handle: the parent creates the listener, records it in the __ashes_worker_listener global (so
    /// its own listen() returns it) and in the ASHES_WORKER_FD environment variable, then relaunches
    /// itself (count - 1) times with CreateProcessA(bInheritHandles=TRUE). A relaunched worker sees
    /// ASHES_WORKER_FD set, records the inherited handle in the global (its listen() returns it), and
    /// does not spawn further. All processes accept on the one shared listener; the kernel serializes
    /// accept across them.
    /// </summary>
    // Parses a decimal C string (at cstrPtr) into an i64, stopping at the first non-digit. No sign.
    private static LlvmValueHandle EmitCStringToI64(LlvmCodegenState state, LlvmValueHandle cstrPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle accSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_acc");
        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_i");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), accSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), idxSlot);
        LlvmBasicBlockHandle loop = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop");
        LlvmBasicBlockHandle body = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_body");
        LlvmBasicBlockHandle done = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmApi.BuildBr(builder, loop);
        LlvmApi.PositionBuilderAtEnd(builder, loop);
        LlvmValueHandle i = LlvmApi.BuildLoad2(builder, state.I64, idxSlot, prefix + "_i_val");
        LlvmValueHandle cptr = LlvmApi.BuildGEP2(builder, state.I8, cstrPtr, [i], prefix + "_cptr");
        LlvmValueHandle c = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I8, cptr, prefix + "_c"), state.I64, prefix + "_c64");
        LlvmValueHandle ge0 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, c, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_ge0");
        LlvmValueHandle le9 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, c, LlvmApi.ConstInt(state.I64, (byte)'9', 0), prefix + "_le9");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildAnd(builder, ge0, le9, prefix + "_digit"), body, done);
        LlvmApi.PositionBuilderAtEnd(builder, body);
        LlvmValueHandle acc = LlvmApi.BuildLoad2(builder, state.I64, accSlot, prefix + "_acc_val");
        LlvmValueHandle digit = LlvmApi.BuildSub(builder, c, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_dig");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, LlvmApi.BuildMul(builder, acc, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_x10"), digit, prefix + "_nacc"), accSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, i, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_ni"), idxSlot);
        LlvmApi.BuildBr(builder, loop);
        LlvmApi.PositionBuilderAtEnd(builder, done);
        return LlvmApi.BuildLoad2(builder, state.I64, accSlot, prefix + "_result");
    }

    // Writes a non-negative i64 as a NUL-terminated decimal string into a >= 24-byte buffer.
    private static void EmitI64ToCString(LlvmCodegenState state, LlvmValueHandle value, LlvmValueHandle bufPtr, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Fill a 24-byte scratch from the end, then copy the used tail forward into bufPtr.
        LlvmTypeHandle tmpType = LlvmApi.ArrayType2(state.I8, 24);
        LlvmValueHandle tmp = LlvmApi.BuildAlloca(builder, tmpType, prefix + "_tmp");
        LlvmValueHandle tmpPtr = GetArrayElementPointer(state, tmpType, tmp, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_tmp_ptr");
        LlvmValueHandle nSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_n");
        LlvmValueHandle posSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_pos");
        LlvmApi.BuildStore(builder, value, nSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 23, 0), posSlot);
        LlvmBasicBlockHandle loop = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop");
        LlvmBasicBlockHandle body = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_body");
        LlvmBasicBlockHandle copy = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_copy");
        LlvmBasicBlockHandle copyBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_copy_body");
        LlvmBasicBlockHandle copyDone = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_copy_done");
        LlvmApi.BuildBr(builder, loop);
        LlvmApi.PositionBuilderAtEnd(builder, loop);
        LlvmValueHandle n = LlvmApi.BuildLoad2(builder, state.I64, nSlot, prefix + "_n_val");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, n, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_nz"), body, copy);
        LlvmApi.PositionBuilderAtEnd(builder, body);
        LlvmValueHandle pos = LlvmApi.BuildLoad2(builder, state.I64, posSlot, prefix + "_pos_val");
        LlvmValueHandle rem = LlvmApi.BuildURem(builder, n, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_rem");
        LlvmValueHandle ch = LlvmApi.BuildTrunc(builder, LlvmApi.BuildAdd(builder, rem, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_ch64"), state.I8, prefix + "_ch");
        LlvmApi.BuildStore(builder, ch, LlvmApi.BuildGEP2(builder, state.I8, tmpPtr, [pos], prefix + "_slot"));
        LlvmApi.BuildStore(builder, LlvmApi.BuildUDiv(builder, n, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_div"), nSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, pos, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_pos_dec"), posSlot);
        LlvmApi.BuildBr(builder, loop);
        LlvmApi.PositionBuilderAtEnd(builder, copy);
        // If value was 0, pos is still 23 and nothing was written; write a single '0'.
        LlvmValueHandle endPos = LlvmApi.BuildLoad2(builder, state.I64, posSlot, prefix + "_endpos");
        LlvmValueHandle wasZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, endPos, LlvmApi.ConstInt(state.I64, 23, 0), prefix + "_was_zero");
        LlvmBasicBlockHandle zeroBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_zero");
        LlvmApi.BuildCondBr(builder, wasZero, zeroBlock, copyBody);
        LlvmApi.PositionBuilderAtEnd(builder, zeroBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, (byte)'0', 0), bufPtr);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, 0, 0), LlvmApi.BuildGEP2(builder, state.I8, bufPtr, [LlvmApi.ConstInt(state.I64, 1, 0)], prefix + "_zterm"));
        LlvmApi.BuildBr(builder, copyDone);
        // Copy tmp[endPos+1 .. 24) to bufPtr, then NUL-terminate.
        LlvmApi.PositionBuilderAtEnd(builder, copyBody);
        LlvmValueHandle srcSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_src");
        LlvmValueHandle dstSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_dst");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, endPos, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_src0"), srcSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), dstSlot);
        LlvmBasicBlockHandle cl = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_cl");
        LlvmBasicBlockHandle clBody = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_cl_body");
        LlvmApi.BuildBr(builder, cl);
        LlvmApi.PositionBuilderAtEnd(builder, cl);
        LlvmValueHandle src = LlvmApi.BuildLoad2(builder, state.I64, srcSlot, prefix + "_src_val");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, src, LlvmApi.ConstInt(state.I64, 24, 0), prefix + "_more"), clBody, copyDone);
        LlvmApi.PositionBuilderAtEnd(builder, clBody);
        LlvmValueHandle dst = LlvmApi.BuildLoad2(builder, state.I64, dstSlot, prefix + "_dst_val");
        LlvmValueHandle b = LlvmApi.BuildLoad2(builder, state.I8, LlvmApi.BuildGEP2(builder, state.I8, tmpPtr, [src], prefix + "_srcp"), prefix + "_b");
        LlvmApi.BuildStore(builder, b, LlvmApi.BuildGEP2(builder, state.I8, bufPtr, [dst], prefix + "_dstp"));
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, src, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_src_inc"), srcSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, dst, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_dst_inc"), dstSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, 0, 0), LlvmApi.BuildGEP2(builder, state.I8, bufPtr, [LlvmApi.BuildAdd(builder, dst, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_term_at")], prefix + "_termp"));
        LlvmApi.BuildBr(builder, cl);
        LlvmApi.PositionBuilderAtEnd(builder, copyDone);
    }

    // Creates a bound, listening, non-blocking TCP socket on INADDR_ANY:port (Windows). Returns the
    // socket handle, or a negative value on failure.
    private static LlvmValueHandle EmitWindowsCreateListenerSocket(LlvmCodegenState state, LlvmValueHandle port, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle wsadataType = LlvmApi.ArrayType2(state.I8, 512);
        LlvmValueHandle wsadata = LlvmApi.BuildAlloca(builder, wsadataType, prefix + "_wsadata");
        EmitWindowsWsaStartup(state, GetArrayElementPointer(state, wsadataType, wsadata, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_wsadata_ptr"), prefix + "_wsastartup");
        LlvmValueHandle socketFd = EmitWindowsSocket(state, 2, 1, 6, prefix + "_socket");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)-1L), 1), resultSlot);
        LlvmBasicBlockHandle bindBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_bind");
        LlvmBasicBlockHandle listenBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_listen");
        LlvmBasicBlockHandle okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_ok");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, socketFd, LlvmApi.ConstInt(state.I64, unchecked((ulong)-1L), 0), prefix + "_socket_ok"),
            bindBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, bindBlock);
        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, prefix + "_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_sockaddr_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, prefix + "_sockaddr_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 8, 0)], prefix + "_sockaddr_tail"), state.I64Ptr, prefix + "_sockaddr_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, prefix + "_family_ptr"));
        LlvmValueHandle portPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 2, 0)], prefix + "_port_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, EmitByteSwap16(state, port, prefix + "_port_network"), i16, prefix + "_port_i16"), LlvmApi.BuildBitCast(builder, portPtr, i16Ptr, prefix + "_port_ptr"));
        LlvmValueHandle bindResult = EmitWindowsBind(state, socketFd, sockaddrBytes, 16, prefix + "_bind_call");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, bindResult, LlvmApi.ConstInt(state.I32, 0, 0), prefix + "_bind_ok"), listenBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, listenBlock);
        LlvmValueHandle listenResult = EmitWindowsListen(state, socketFd, 128, prefix + "_listen_call");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, listenResult, LlvmApi.ConstInt(state.I32, 0, 0), prefix + "_listen_ok"), okBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
        EmitSetSocketNonBlocking(state, socketFd, prefix + "_nonblocking");
        LlvmApi.BuildStore(builder, socketFd, resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_fd");
    }

    private static LlvmValueHandle EmitTcpConnect(LlvmCodegenState state, LlvmValueHandle hostRef, LlvmValueHandle port)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxTcpConnect(state, hostRef, port)
            : EmitWindowsTcpConnect(state, hostRef, port);
    }

    private static LlvmValueHandle EmitTcpSend(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle textRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxTcpSend(state, socket, textRef)
            : EmitWindowsTcpSend(state, socket, textRef);
    }

    private static LlvmValueHandle EmitTcpReceive(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle maxBytes)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxTcpReceive(state, socket, maxBytes)
            : EmitWindowsTcpReceive(state, socket, maxBytes);
    }

    private static LlvmValueHandle EmitTcpClose(LlvmCodegenState state, LlvmValueHandle socket)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxTcpClose(state, socket)
            : EmitWindowsTcpClose(state, socket);
    }

    private static LlvmValueHandle EmitLinuxTcpConnect(LlvmCodegenState state, LlvmValueHandle hostRef, LlvmValueHandle port)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_connect_result");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_connect_socket");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), socketSlot);
        LlvmValueHandle resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "tcp_connect_resolve");
        LlvmValueHandle resolveTag = LoadMemory(state, resolveResult, 0, "tcp_connect_resolve_tag");
        LlvmValueHandle resolveFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resolveTag, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_resolve_failed");
        var resolveErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_resolve_error");
        var validatePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_validate_port");
        var openSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_open_socket");
        var connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_connect");
        var connectFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_fail");
        var connectCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_close_socket");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_continue");
        LlvmApi.BuildCondBr(builder, resolveFailed, resolveErrorBlock, validatePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resolveErrorBlock);
        LlvmApi.BuildStore(builder, resolveResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validatePortBlock);
        LlvmValueHandle validPort = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, port, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_port_gt_zero"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, port, LlvmApi.ConstInt(state.I64, 65535, 0), "tcp_connect_port_le_max"),
            "tcp_connect_port_valid");
        LlvmApi.BuildCondBr(builder, validPort, openSocketBlock, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openSocketBlock);
        LlvmValueHandle socketValue = EmitLinuxSyscall(
            state,
            SyscallSocket,
            LlvmApi.ConstInt(state.I64, 2, 0),
            LlvmApi.ConstInt(state.I64, 1, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "tcp_connect_socket_call");
        LlvmApi.BuildStore(builder, socketValue, socketSlot);
        LlvmValueHandle socketFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, socketValue, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_socket_failed");
        LlvmApi.BuildCondBr(builder, socketFailed, connectFailBlock, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectBlock);
        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, "tcp_connect_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_sockaddr_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle sockaddrI64Ptr = LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, "tcp_connect_sockaddr_i64");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), sockaddrI64Ptr);
        LlvmValueHandle sockaddrTailPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 8, 0)], "tcp_connect_sockaddr_tail");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrTailPtr, state.I64Ptr, "tcp_connect_sockaddr_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, "tcp_connect_family_ptr"));
        LlvmValueHandle portPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 2, 0)], "tcp_connect_port_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, EmitByteSwap16(state, port, "tcp_connect_port_network"), i16, "tcp_connect_port_i16"), LlvmApi.BuildBitCast(builder, portPtr, i16Ptr, "tcp_connect_port_ptr"));
        LlvmValueHandle addrPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 4, 0)], "tcp_connect_addr_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, LoadMemory(state, resolveResult, 8, "tcp_connect_addr_value"), state.I32, "tcp_connect_addr_i32"), LlvmApi.BuildBitCast(builder, addrPtr, state.I32Ptr, "tcp_connect_addr_ptr"));
        LlvmValueHandle connectResult = EmitLinuxSyscall(
            state,
            SyscallConnect,
            LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_socket_value"),
            LlvmApi.BuildPtrToInt(builder, sockaddrBytes, state.I64, "tcp_connect_sockaddr_ptr"),
            LlvmApi.ConstInt(state.I64, 16, 0),
            "tcp_connect_call");
        LlvmValueHandle connectFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, connectResult, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_failed_bool");
        var connectSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_success");
        LlvmApi.BuildCondBr(builder, connectFailed, connectCloseBlock, connectSuccessBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectCloseBlock);
        LlvmValueHandle connectCloseSocket = LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_close_socket_value");
        _ = EmitNetworkingRuntimeCall(state, "ashes_epoll_forget", [connectCloseSocket], "tcp_connect_close_forget");
        EmitLinuxSyscall(state, SyscallClose, connectCloseSocket, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_close_call");
        LlvmApi.BuildBr(builder, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectFailBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpConnectFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectSuccessBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_success_socket")), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tcp_connect_result_value");
    }

    private static LlvmValueHandle EmitWindowsTcpConnect(LlvmCodegenState state, LlvmValueHandle hostRef, LlvmValueHandle port)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_connect_win_result");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_connect_win_socket");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), socketSlot);
        LlvmValueHandle resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "tcp_connect_win_resolve");
        LlvmValueHandle resolveTag = LoadMemory(state, resolveResult, 0, "tcp_connect_win_resolve_tag");
        LlvmValueHandle resolveFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resolveTag, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_win_resolve_failed");
        var resolveErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_resolve_error");
        var validatePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_validate_port");
        var initWinsockBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_init_winsock");
        var openSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_open_socket");
        var connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_connect");
        var connectCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_close_socket");
        var connectFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_fail");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_continue");
        LlvmApi.BuildCondBr(builder, resolveFailed, resolveErrorBlock, validatePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resolveErrorBlock);
        LlvmApi.BuildStore(builder, resolveResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validatePortBlock);
        LlvmValueHandle validPort = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, port, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_win_port_gt_zero"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, port, LlvmApi.ConstInt(state.I64, 65535, 0), "tcp_connect_win_port_le_max"),
            "tcp_connect_win_port_valid");
        LlvmApi.BuildCondBr(builder, validPort, initWinsockBlock, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initWinsockBlock);
        LlvmTypeHandle wsadataType = LlvmApi.ArrayType2(state.I8, 512);
        LlvmValueHandle wsadata = LlvmApi.BuildAlloca(builder, wsadataType, "tcp_connect_win_wsadata");
        LlvmValueHandle winsockStarted = EmitWindowsWsaStartup(state, GetArrayElementPointer(state, wsadataType, wsadata, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_win_wsadata_ptr"), "tcp_connect_win_wsastartup");
        LlvmApi.BuildCondBr(builder, winsockStarted, openSocketBlock, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openSocketBlock);
        LlvmValueHandle socketValue = EmitWindowsSocket(state, 2, 1, 6, "tcp_connect_win_socket_call");
        LlvmApi.BuildStore(builder, socketValue, socketSlot);
        LlvmValueHandle socketFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, socketValue, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), "tcp_connect_win_socket_failed");
        LlvmApi.BuildCondBr(builder, socketFailed, connectFailBlock, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectBlock);
        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, "tcp_connect_win_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_win_sockaddr_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle sockaddrI64Ptr = LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, "tcp_connect_win_sockaddr_i64");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), sockaddrI64Ptr);
        LlvmValueHandle sockaddrTailPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 8, 0)], "tcp_connect_win_sockaddr_tail");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrTailPtr, state.I64Ptr, "tcp_connect_win_sockaddr_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, "tcp_connect_win_family_ptr"));
        LlvmValueHandle portPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 2, 0)], "tcp_connect_win_port_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, EmitByteSwap16(state, port, "tcp_connect_win_port_network"), i16, "tcp_connect_win_port_i16"), LlvmApi.BuildBitCast(builder, portPtr, i16Ptr, "tcp_connect_win_port_ptr"));
        LlvmValueHandle addrPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 4, 0)], "tcp_connect_win_addr_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, LoadMemory(state, resolveResult, 8, "tcp_connect_win_addr_value"), state.I32, "tcp_connect_win_addr_i32"), LlvmApi.BuildBitCast(builder, addrPtr, state.I32Ptr, "tcp_connect_win_addr_ptr"));
        LlvmValueHandle connectResult = EmitWindowsConnect(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_win_socket_value"), sockaddrBytes, "tcp_connect_win_connect_call");
        var connectSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_success");
        LlvmApi.BuildCondBr(builder, connectResult, connectSuccessBlock, connectCloseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectCloseBlock);
        EmitWindowsCloseSocket(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_win_close_socket_value"), "tcp_connect_win_close_socket_call");
        LlvmApi.BuildBr(builder, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectFailBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpConnectFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectSuccessBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_win_success_socket")), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tcp_connect_win_result_value");
    }

    private static LlvmValueHandle EmitLinuxTcpSend(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_result");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_cursor");
        LlvmValueHandle totalLen = LoadStringLength(state, textRef, "tcp_send_total_len");
        LlvmApi.BuildStore(builder, totalLen, remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, textRef, "tcp_send_cursor_start"), cursorSlot);
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_loop_body");
        var updateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_update");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_fail");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_continue");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "tcp_send_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_send_done");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_done_block");
        LlvmApi.BuildCondBr(builder, done, doneBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle sent = EmitLinuxSyscall(state, SyscallWrite, socket, LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "tcp_send_cursor_value"), remaining, "tcp_send_syscall");
        LlvmValueHandle sendFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, sent, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_send_failed");
        LlvmApi.BuildCondBr(builder, sendFailed, failBlock, updateBlock);

        LlvmApi.PositionBuilderAtEnd(builder, updateBlock);
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "tcp_send_cursor_current");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, sent, "tcp_send_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursor, sent, "tcp_send_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, totalLen), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tcp_send_result_value");
    }

    private static LlvmValueHandle EmitWindowsTcpSend(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_win_result");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_win_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_win_cursor");
        LlvmValueHandle totalLen = LoadStringLength(state, textRef, "tcp_send_win_total_len");
        LlvmApi.BuildStore(builder, totalLen, remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, textRef, "tcp_send_win_cursor_start"), cursorSlot);
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_loop_body");
        var updateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_update");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_fail");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_continue");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "tcp_send_win_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_send_win_done");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_done_block");
        LlvmApi.BuildCondBr(builder, done, doneBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle chunk = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, remaining, LlvmApi.ConstInt(state.I64, int.MaxValue, 0), "tcp_send_win_chunk_gt"),
            LlvmApi.ConstInt(state.I64, int.MaxValue, 0),
            remaining,
            "tcp_send_win_chunk");
        LlvmValueHandle sentRaw = EmitWindowsSend(state, socket, LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "tcp_send_win_cursor_value"), state.I8Ptr, "tcp_send_win_cursor_ptr"), LlvmApi.BuildTrunc(builder, chunk, state.I32, "tcp_send_win_chunk_i32"), "tcp_send_win_call");
        LlvmValueHandle sendFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, sentRaw, LlvmApi.ConstInt(state.I32, 0, 1), "tcp_send_win_failed");
        LlvmApi.BuildCondBr(builder, sendFailed, failBlock, updateBlock);

        LlvmApi.PositionBuilderAtEnd(builder, updateBlock);
        LlvmValueHandle sent = LlvmApi.BuildSExt(builder, sentRaw, state.I64, "tcp_send_win_sent");
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "tcp_send_win_cursor_current");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, sent, "tcp_send_win_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursor, sent, "tcp_send_win_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, totalLen), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tcp_send_win_result_value");
    }

    private static LlvmValueHandle EmitLinuxTcpReceive(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle maxBytes)
    {
        return EmitTcpReceiveCommon(state, socket, maxBytes, "tcp_receive", static (s, sock, bytesPtr, max, name) => EmitLinuxSyscall(s, SyscallRead, sock, LlvmApi.BuildPtrToInt(s.Target.Builder, bytesPtr, s.I64, name + "_ptr"), LlvmApi.BuildSExt(s.Target.Builder, max, s.I64, name + "_len"), name));
    }

    private static LlvmValueHandle EmitWindowsTcpReceive(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle maxBytes)
    {
        return EmitTcpReceiveCommon(state, socket, maxBytes, "tcp_receive_win", static (s, sock, bytesPtr, max, name) => LlvmApi.BuildSExt(s.Target.Builder, EmitWindowsRecv(s, sock, bytesPtr, max, name), s.I64, name + "_sext"));
    }

    private static LlvmValueHandle EmitTcpReceiveCommon(
        LlvmCodegenState state,
        LlvmValueHandle socket,
        LlvmValueHandle maxBytes,
        string prefix,
        Func<LlvmCodegenState, LlvmValueHandle, LlvmValueHandle, LlvmValueHandle, string, LlvmValueHandle> emitRead)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        var invalidMaxBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid_max");
        var readBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_read");
        var handleReadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_handle_read");
        var invalidUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid_utf8");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmValueHandle positiveMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, maxBytes, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_positive_max");
        LlvmApi.BuildCondBr(builder, positiveMax, readBlock, invalidMaxBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidMaxBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidMaxBytesMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readBlock);
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, maxBytes, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_size"));
        StoreMemory(state, stringRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_len_init");
        LlvmValueHandle readCount = emitRead(state, socket, GetStringBytesPointer(state, stringRef, prefix + "_bytes"), LlvmApi.BuildTrunc(builder, maxBytes, state.I32, prefix + "_max_i32"), prefix + "_read_call");
        LlvmValueHandle readFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, readCount, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_read_failed");
        LlvmApi.BuildCondBr(builder, readFailed, failBlock, handleReadBlock);

        LlvmApi.PositionBuilderAtEnd(builder, handleReadBlock);
        StoreMemory(state, stringRef, 0, readCount, prefix + "_len_store");
        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readCount, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_empty");
        var successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        var validateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_validate");
        LlvmApi.BuildCondBr(builder, isEmpty, successBlock, validateBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validateBlock);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(state, GetStringBytesPointer(state, stringRef, prefix + "_validate_bytes"), readCount, prefix + "_utf8");
        LlvmValueHandle valid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_utf8_valid");
        LlvmApi.BuildCondBr(builder, valid, successBlock, invalidUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, invalidUtf8Block);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidUtf8Message)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, stringRef), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpReceiveFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static LlvmValueHandle EmitLinuxTcpClose(LlvmCodegenState state, LlvmValueHandle socket)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        // Clear the persistent-epoll mask entry so a reused fd number re-registers correctly (the kernel
        // drops the fd from the epoll set on close, but our per-fd table must be cleared too).
        _ = EmitNetworkingRuntimeCall(state, "ashes_epoll_forget", [socket], "tcp_close_forget");
        LlvmValueHandle result = EmitLinuxSyscall(state, SyscallClose, socket, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "tcp_close_call");
        LlvmValueHandle success = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, result, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_close_success");
        return LlvmApi.BuildSelect(builder, success, EmitResultOk(state, EmitUnitValue(state)), EmitResultError(state, EmitHeapStringLiteral(state, TcpCloseFailedMessage)), "tcp_close_result");
    }

    private static LlvmValueHandle EmitWindowsTcpClose(LlvmCodegenState state, LlvmValueHandle socket)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle closeResult = EmitWindowsCloseSocket(state, socket, "tcp_close_win_call");
        LlvmValueHandle success = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, closeResult, LlvmApi.ConstInt(state.I32, 0, 0), "tcp_close_win_success");
        return LlvmApi.BuildSelect(builder, success, EmitResultOk(state, EmitUnitValue(state)), EmitResultError(state, EmitHeapStringLiteral(state, TcpCloseFailedMessage)), "tcp_close_win_result");
    }

    private static LlvmValueHandle EmitResolveHostIpv4OrLocalhost(LlvmCodegenState state, LlvmValueHandle hostRef, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmValueHandle partSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_part");
        LlvmValueHandle currentSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_current");
        LlvmValueHandle seenDigitSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_seen_digit");
        LlvmValueHandle addressSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_address");
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpResolveFailedMessage)), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), partSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), currentSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), seenDigitSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), addressSlot);

        LlvmValueHandle localhostEquals = EmitStringComparison(state, hostRef, EmitStackStringObject(state, "localhost"));
        LlvmValueHandle isLocalhost = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, localhostEquals, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_localhost");
        LlvmValueHandle hostLen = LoadStringLength(state, hostRef, prefix + "_host_len");
        LlvmValueHandle hostBytes = GetStringBytesPointer(state, hostRef, prefix + "_host_bytes");
        var localhostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_localhost");
        var parseLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_loop");
        var parseInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_inspect");
        var digitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_digit");
        var dotBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dot");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail");
        var finalizeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_finalize");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmApi.BuildCondBr(builder, isLocalhost, localhostBlock, parseLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, localhostBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0x0100007FUL, 0)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseLoopBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, index, hostLen, prefix + "_done");
        LlvmApi.BuildCondBr(builder, done, finalizeBlock, parseInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseInspectBlock);
        LlvmValueHandle currentByte = LoadByteAt(state, hostBytes, index, prefix + "_current_byte");
        LlvmValueHandle currentByte64 = LlvmApi.BuildZExt(builder, currentByte, state.I64, prefix + "_current_byte_i64");
        LlvmValueHandle isDigit = BuildByteRangeCheck(state, currentByte64, (byte)'0', (byte)'9', prefix + "_digit_range");
        var dotCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dot_check");
        LlvmApi.BuildCondBr(builder, isDigit, digitBlock, dotCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, dotCheckBlock);
        LlvmValueHandle isDot = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, (byte)'.', 0), prefix + "_is_dot");
        LlvmApi.BuildCondBr(builder, isDot, dotBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, digitBlock);
        LlvmValueHandle currentValue = LlvmApi.BuildLoad2(builder, state.I64, currentSlot, prefix + "_current_value");
        LlvmValueHandle parsedDigit = LlvmApi.BuildSub(builder, currentByte64, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_parsed_digit");
        LlvmValueHandle nextValue = LlvmApi.BuildAdd(builder, LlvmApi.BuildMul(builder, currentValue, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_mul"), parsedDigit, prefix + "_next_value");
        LlvmValueHandle valueTooLarge = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, nextValue, LlvmApi.ConstInt(state.I64, 255, 0), prefix + "_value_too_large");
        var storeDigitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_digit");
        LlvmApi.BuildCondBr(builder, valueTooLarge, failBlock, storeDigitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeDigitBlock);
        LlvmApi.BuildStore(builder, nextValue, currentSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), seenDigitSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, parseLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, dotBlock);
        LlvmValueHandle seenDigit = LlvmApi.BuildLoad2(builder, state.I64, seenDigitSlot, prefix + "_seen_digit_value");
        LlvmValueHandle part = LlvmApi.BuildLoad2(builder, state.I64, partSlot, prefix + "_part_value");
        LlvmValueHandle dotValid = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, seenDigit, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_dot_seen_digit"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, part, LlvmApi.ConstInt(state.I64, 3, 0), prefix + "_dot_part_lt_three"),
            prefix + "_dot_valid");
        var storeDotBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_dot");
        LlvmApi.BuildCondBr(builder, dotValid, storeDotBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeDotBlock);
        LlvmValueHandle addressValue = LlvmApi.BuildLoad2(builder, state.I64, addressSlot, prefix + "_address_value");
        LlvmValueHandle shiftedOctet = LlvmApi.BuildShl(builder, LlvmApi.BuildLoad2(builder, state.I64, currentSlot, prefix + "_octet_value"), LlvmApi.BuildMul(builder, part, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_octet_shift"), prefix + "_shifted_octet");
        LlvmApi.BuildStore(builder, LlvmApi.BuildOr(builder, addressValue, shiftedOctet, prefix + "_address_next"), addressSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, part, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_part_next"), partSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), currentSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), seenDigitSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_after_dot"), indexSlot);
        LlvmApi.BuildBr(builder, parseLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finalizeBlock);
        LlvmValueHandle finalSeenDigit = LlvmApi.BuildLoad2(builder, state.I64, seenDigitSlot, prefix + "_final_seen_digit");
        LlvmValueHandle finalPart = LlvmApi.BuildLoad2(builder, state.I64, partSlot, prefix + "_final_part");
        LlvmValueHandle finalValid = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, finalSeenDigit, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_final_seen_digit_ok"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, finalPart, LlvmApi.ConstInt(state.I64, 3, 0), prefix + "_final_part_eq_three"),
            prefix + "_final_valid");
        var storeFinalBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_final");
        LlvmApi.BuildCondBr(builder, finalValid, storeFinalBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeFinalBlock);
        LlvmValueHandle finalAddress = LlvmApi.BuildOr(builder,
            LlvmApi.BuildLoad2(builder, state.I64, addressSlot, prefix + "_address_before_final"),
            LlvmApi.BuildShl(builder, LlvmApi.BuildLoad2(builder, state.I64, currentSlot, prefix + "_current_before_final"), LlvmApi.ConstInt(state.I64, 24, 0), prefix + "_final_shifted_octet"),
            prefix + "_final_address");
        LlvmApi.BuildStore(builder, EmitResultOk(state, finalAddress), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }
}
