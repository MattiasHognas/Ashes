using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private static LlvmValueHandle EmitWindowsWsaStartup(LlvmCodegenState state, LlvmValueHandle wsadataPtr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle wsaStartupType = LlvmApi.FunctionType(state.I32, [i16, state.I8Ptr]);
        LlvmValueHandle wsaStartupPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWsaStartupImport,
            name + "_ptr");
        LlvmValueHandle result = LlvmApi.BuildCall2(builder,
            wsaStartupType,
            wsaStartupPtr,
            [
                LlvmApi.ConstInt(i16, 0x0202, 0),
                wsadataPtr
            ],
            name);
        return LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, result, LlvmApi.ConstInt(state.I32, 0, 0), name + "_success");
    }

    private static LlvmValueHandle EmitWindowsSocket(LlvmCodegenState state, int af, int socketTypeValue, int protocol, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle socketType = LlvmApi.FunctionType(state.I64, [state.I32, state.I32, state.I32]);
        LlvmValueHandle socketPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsSocketImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            socketType,
            socketPtr,
            [
                LlvmApi.ConstInt(state.I32, (uint)af, 0),
                LlvmApi.ConstInt(state.I32, (uint)socketTypeValue, 0),
                LlvmApi.ConstInt(state.I32, (uint)protocol, 0)
            ],
            name);
    }

    private static LlvmValueHandle EmitWindowsConnect(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle sockaddrPtr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle connectType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32]);
        LlvmValueHandle connectPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsConnectImport,
            name + "_ptr");
        LlvmValueHandle result = LlvmApi.BuildCall2(builder,
            connectType,
            connectPtr,
            [
                socket,
                sockaddrPtr,
                LlvmApi.ConstInt(state.I32, 16, 0)
            ],
            name);
        return LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, result, LlvmApi.ConstInt(state.I32, 0, 0), name + "_success");
    }

    private static LlvmValueHandle EmitWindowsSend(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle buffer, LlvmValueHandle len, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle sendType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32]);
        LlvmValueHandle sendPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsSendImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            sendType,
            sendPtr,
            [
                socket,
                buffer,
                len,
                LlvmApi.ConstInt(state.I32, 0, 0)
            ],
            name);
    }

    private static LlvmValueHandle EmitWindowsRecv(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle buffer, LlvmValueHandle len, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle recvType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32]);
        LlvmValueHandle recvPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsRecvImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            recvType,
            recvPtr,
            [
                socket,
                buffer,
                len,
                LlvmApi.ConstInt(state.I32, 0, 0)
            ],
            name);
    }

    private static LlvmValueHandle EmitWindowsCloseSocket(LlvmCodegenState state, LlvmValueHandle socket, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle closeSocketType = LlvmApi.FunctionType(state.I32, [state.I64]);
        LlvmValueHandle closeSocketPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsCloseSocketImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            closeSocketType,
            closeSocketPtr,
            [socket],
            name);
    }

    private static LlvmValueHandle EmitWindowsIoctlSocket(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle command, LlvmValueHandle valuePtr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle ioctlSocketType = LlvmApi.FunctionType(state.I32, [state.I64, state.I32, state.I64Ptr]);
        LlvmValueHandle ioctlSocketPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsIoctlSocketImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            ioctlSocketType,
            ioctlSocketPtr,
            [
                socket,
                LlvmApi.BuildTrunc(builder, command, state.I32, name + "_cmd"),
                valuePtr
            ],
            name);
    }

    private static LlvmValueHandle EmitWindowsWsaGetLastError(LlvmCodegenState state, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle getLastErrorType = LlvmApi.FunctionType(state.I32, []);
        LlvmValueHandle getLastErrorPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWsaGetLastErrorImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder, getLastErrorType, getLastErrorPtr, Array.Empty<LlvmValueHandle>(), name);
    }

    private static LlvmValueHandle EmitWindowsWsaPoll(LlvmCodegenState state, LlvmValueHandle pollfdPtr, LlvmValueHandle count, LlvmValueHandle timeoutMs, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle wsaPollType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I32, state.I32]);
        LlvmValueHandle wsaPollPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWsaPollImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            wsaPollType,
            wsaPollPtr,
            [
                pollfdPtr,
                LlvmApi.BuildTrunc(builder, count, state.I32, name + "_count"),
                LlvmApi.BuildTrunc(builder, timeoutMs, state.I32, name + "_timeout")
            ],
            name);
    }

    private static LlvmValueHandle EmitWindowsLoadLibrary(LlvmCodegenState state, LlvmValueHandle pathCstr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmValueHandle functionPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsLoadLibraryImport,
            name + "_ptr");
        LlvmValueHandle handlePtr = LlvmApi.BuildCall2(builder, functionType, functionPtr, [pathCstr], name);
        return LlvmApi.BuildPtrToInt(builder, handlePtr, state.I64, name + "_handle");
    }

    private static LlvmValueHandle EmitWindowsGetProcAddress(LlvmCodegenState state, LlvmValueHandle moduleHandle, LlvmValueHandle symbolCstr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle functionPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsGetProcAddressImport,
            name + "_ptr");
        LlvmValueHandle modulePtr = LlvmApi.BuildIntToPtr(builder, moduleHandle, state.I8Ptr, name + "_module_ptr");
        LlvmValueHandle addressPtr = LlvmApi.BuildCall2(builder, functionType, functionPtr, [modulePtr, symbolCstr], name);
        return LlvmApi.BuildPtrToInt(builder, addressPtr, state.I64, name + "_address");
    }

    private static LlvmValueHandle EmitWindowsCertOpenSystemStore(LlvmCodegenState state, LlvmValueHandle storeNameCstr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8Ptr, [state.I64, state.I8Ptr]);
        LlvmValueHandle functionPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsCertOpenSystemStoreImport,
            name + "_ptr");
        LlvmValueHandle storePtr = LlvmApi.BuildCall2(builder, functionType, functionPtr, [LlvmApi.ConstInt(state.I64, 0, 0), storeNameCstr], name);
        return LlvmApi.BuildPtrToInt(builder, storePtr, state.I64, name + "_handle");
    }

    private static LlvmValueHandle EmitWindowsCertEnumCertificatesInStore(LlvmCodegenState state, LlvmValueHandle storeHandle, LlvmValueHandle previousContextHandle, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle functionPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsCertEnumCertificatesInStoreImport,
            name + "_ptr");
        LlvmValueHandle storePtr = LlvmApi.BuildIntToPtr(builder, storeHandle, state.I8Ptr, name + "_store_ptr");
        LlvmValueHandle previousPtr = LlvmApi.BuildIntToPtr(builder, previousContextHandle, state.I8Ptr, name + "_previous_ptr");
        LlvmValueHandle certificatePtr = LlvmApi.BuildCall2(builder, functionType, functionPtr, [storePtr, previousPtr], name);
        return LlvmApi.BuildPtrToInt(builder, certificatePtr, state.I64, name + "_handle");
    }

    private static void EmitWindowsCertCloseStore(LlvmCodegenState state, LlvmValueHandle storeHandle, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I32]);
        LlvmValueHandle functionPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsCertCloseStoreImport,
            name + "_ptr");
        LlvmValueHandle storePtr = LlvmApi.BuildIntToPtr(builder, storeHandle, state.I8Ptr, name + "_store_ptr");
        _ = LlvmApi.BuildCall2(builder, functionType, functionPtr, [storePtr, LlvmApi.ConstInt(state.I32, 0, 0)], name);
    }

    private static LlvmValueHandle EmitWindowsBind(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle sockaddrPtr, int sockaddrLen, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle bindType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32]);
        LlvmValueHandle bindPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsBindImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            bindType,
            bindPtr,
            [
                socket,
                sockaddrPtr,
                LlvmApi.ConstInt(state.I32, unchecked((uint)sockaddrLen), 0)
            ],
            name);
    }

    private static LlvmValueHandle EmitWindowsSetSockOpt(LlvmCodegenState state, LlvmValueHandle socket, int level, int optionName, LlvmValueHandle optionValuePtr, int optionLength, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle setSockOptType = LlvmApi.FunctionType(state.I32, [state.I64, state.I32, state.I32, state.I8Ptr, state.I32]);
        LlvmValueHandle setSockOptPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsSetSockOptImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            setSockOptType,
            setSockOptPtr,
            [
                socket,
                LlvmApi.ConstInt(state.I32, unchecked((uint)level), 1),
                LlvmApi.ConstInt(state.I32, unchecked((uint)optionName), 0),
                optionValuePtr,
                LlvmApi.ConstInt(state.I32, unchecked((uint)optionLength), 0)
            ],
            name);
    }

    private static LlvmValueHandle EmitWindowsWsaIoctl(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle controlCode, LlvmValueHandle inputBuffer, int inputLength, LlvmValueHandle outputBuffer, int outputLength, LlvmValueHandle bytesReturnedPtr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle wsaIoctlType = LlvmApi.FunctionType(state.I32, [state.I64, state.I32, state.I8Ptr, state.I32, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle wsaIoctlPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWsaIoctlImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            wsaIoctlType,
            wsaIoctlPtr,
            [
                socket,
                LlvmApi.BuildTrunc(builder, controlCode, state.I32, name + "_code"),
                inputBuffer,
                LlvmApi.ConstInt(state.I32, unchecked((uint)inputLength), 0),
                outputBuffer,
                LlvmApi.ConstInt(state.I32, unchecked((uint)outputLength), 0),
                bytesReturnedPtr,
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_overlapped"),
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_completion")
            ],
            name);
    }

    private static LlvmValueHandle EmitWindowsCreateIocp(LlvmCodegenState state, LlvmValueHandle fileHandle, LlvmValueHandle existingPort, LlvmValueHandle completionKey, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle createIocpType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64, state.I64, state.I32]);
        LlvmValueHandle createIocpPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsCreateIoCompletionPortImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            createIocpType,
            createIocpPtr,
            [
                fileHandle,
                existingPort,
                completionKey,
                LlvmApi.ConstInt(state.I32, 0, 0)
            ],
            name);
    }

    private static LlvmValueHandle EmitWindowsEnsureIocpPort(LlvmCodegenState state, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle portSlot = LlvmApi.BuildAlloca(builder, state.I64, name + "_port_slot");
        LlvmApi.BuildStore(builder, LoadMemory(state, state.WindowsIocpPortGlobal, 0, name + "_existing_port"), portSlot);
        LlvmValueHandle hasPort = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne,
            LlvmApi.BuildLoad2(builder, state.I64, portSlot, name + "_port_value"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            name + "_has_port");

        LlvmBasicBlockHandle createBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_create");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_done");
        LlvmApi.BuildCondBr(builder, hasPort, doneBlock, createBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBlock);
        LlvmValueHandle createdPort = EmitWindowsCreateIocp(
            state,
            LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            name + "_create_call");
        StoreMemory(state, state.WindowsIocpPortGlobal, 0, createdPort, name + "_store_global");
        LlvmApi.BuildStore(builder, createdPort, portSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, portSlot, name + "_result");
    }

    private static void EmitWindowsAssociateSocketWithIocp(LlvmCodegenState state, LlvmValueHandle socket, string name)
    {
        LlvmValueHandle port = EmitWindowsEnsureIocpPort(state, name + "_ensure_port");
        _ = EmitWindowsCreateIocp(state, socket, port, LlvmApi.ConstInt(state.I64, 0, 0), name + "_associate");
    }

    private static LlvmValueHandle EmitWindowsCreateIocpOperationContext(LlvmCodegenState state, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle context = EmitAllocDynamic(state, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.TotalSize, 0));
        StoreMemory(state, context, WindowsIocpOperationLayout.Status, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StatePending, 0), name + "_status_init");
        StoreMemory(state, context, WindowsIocpOperationLayout.BytesTransferred, LlvmApi.ConstInt(state.I64, 0, 0), name + "_bytes_init");
        StoreMemory(state, context, WindowsIocpOperationLayout.Overlapped + 0, LlvmApi.ConstInt(state.I64, 0, 0), name + "_ov0");
        StoreMemory(state, context, WindowsIocpOperationLayout.Overlapped + 8, LlvmApi.ConstInt(state.I64, 0, 0), name + "_ov1");
        StoreMemory(state, context, WindowsIocpOperationLayout.Overlapped + 16, LlvmApi.ConstInt(state.I64, 0, 0), name + "_ov2");
        StoreMemory(state, context, WindowsIocpOperationLayout.Overlapped + 24, LlvmApi.ConstInt(state.I64, 0, 0), name + "_ov3");
        return context;
    }

    private static LlvmValueHandle EmitWindowsIocpOverlappedPtr(LlvmCodegenState state, LlvmValueHandle operationContext, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        return LlvmApi.BuildIntToPtr(builder,
            LlvmApi.BuildAdd(builder, operationContext, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.Overlapped, 0), name + "_addr"),
            state.I8Ptr,
            name + "_ptr");
    }

    private static LlvmValueHandle EmitWindowsIocpOperationStatus(LlvmCodegenState state, LlvmValueHandle operationContext, string name)
        => LoadMemory(state, operationContext, WindowsIocpOperationLayout.Status, name + "_status");

    private static LlvmValueHandle EmitWindowsIocpBytesTransferred(LlvmCodegenState state, LlvmValueHandle operationContext, string name)
        => LoadMemory(state, operationContext, WindowsIocpOperationLayout.BytesTransferred, name + "_bytes");

    private static LlvmValueHandle EmitWindowsLoadConnectExPointer(LlvmCodegenState state, LlvmValueHandle socket, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle guidType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle guidStorage = LlvmApi.BuildAlloca(builder, guidType, name + "_guid_storage");
        LlvmValueHandle guidBytes = GetArrayElementPointer(state, guidType, guidStorage, LlvmApi.ConstInt(state.I64, 0, 0), name + "_guid_bytes");
        LlvmValueHandle functionPointerSlot = LlvmApi.BuildAlloca(builder, state.I64, name + "_function_ptr_slot");
        LlvmValueHandle bytesReturnedSlot = LlvmApi.BuildAlloca(builder, state.I32, name + "_bytes_returned_slot");
        LlvmApi.BuildStore(builder,
            LlvmApi.ConstInt(state.I64, 0x4660DDF325A207B9, 0),
            LlvmApi.BuildBitCast(builder, guidBytes, state.I64Ptr, name + "_guid_head_ptr"));
        LlvmApi.BuildStore(builder,
            LlvmApi.ConstInt(state.I64, 0x3E06748CE576E98E, 0),
            LlvmApi.BuildBitCast(builder,
                LlvmApi.BuildGEP2(builder, state.I8, guidBytes, [LlvmApi.ConstInt(state.I64, 8, 0)], name + "_guid_tail_bytes"),
                state.I64Ptr,
                name + "_guid_tail_ptr"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), functionPointerSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesReturnedSlot);

        LlvmValueHandle guidPtr = LlvmApi.BuildBitCast(builder, guidBytes, state.I8Ptr, name + "_guid_ptr");
        LlvmValueHandle outputPtr = LlvmApi.BuildBitCast(builder, functionPointerSlot, state.I8Ptr, name + "_output_ptr");
        LlvmValueHandle ioctlResult = EmitWindowsWsaIoctl(
            state,
            socket,
            LlvmApi.ConstInt(state.I64, WindowsSioGetExtensionFunctionPointer, 0),
            guidPtr,
            16,
            outputPtr,
            8,
            bytesReturnedSlot,
            name + "_ioctl");
        LlvmValueHandle ok = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, ioctlResult, LlvmApi.ConstInt(state.I32, 0, 0), name + "_ok");
        return LlvmApi.BuildSelect(builder,
            ok,
            LlvmApi.BuildLoad2(builder, state.I64, functionPointerSlot, name + "_function_ptr"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            name + "_result");
    }

    private static LlvmValueHandle EmitWindowsBindIpv4Any(LlvmCodegenState state, LlvmValueHandle socket, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, name + "_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), name + "_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, name + "_head_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder,
            LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, [LlvmApi.ConstInt(state.I64, 8, 0)], name + "_tail"),
            state.I64Ptr,
            name + "_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, name + "_family_ptr"));
        return EmitWindowsBind(state, socket, sockaddrBytes, 16, name + "_bind");
    }

    private static void EmitWindowsUpdateConnectContext(LlvmCodegenState state, LlvmValueHandle socket, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        EmitWindowsSetSockOpt(
            state,
            socket,
            WindowsSolSocket,
            WindowsSoUpdateConnectContext,
            LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_null_opt"),
            0,
            name + "_setsockopt");
    }

    private static LlvmValueHandle EmitWindowsConnectEx(LlvmCodegenState state, LlvmValueHandle connectExPtrValue, LlvmValueHandle socket, LlvmValueHandle sockaddrPtr, LlvmValueHandle overlappedPtr, LlvmValueHandle bytesSentSlot, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle connectExType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        LlvmValueHandle connectExPtr = LlvmApi.BuildIntToPtr(builder, connectExPtrValue, LlvmApi.PointerTypeInContext(state.Target.Context, 0), name + "_fn_ptr");
        return LlvmApi.BuildCall2(builder,
            connectExType,
            connectExPtr,
            [
                socket,
                sockaddrPtr,
                LlvmApi.ConstInt(state.I32, 16, 0),
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_send_buffer"),
                LlvmApi.ConstInt(state.I32, 0, 0),
                bytesSentSlot,
                overlappedPtr
            ],
            name);
    }

    private static LlvmValueHandle EmitWindowsIssueWsaSend(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle buffer, LlvmValueHandle len, LlvmValueHandle overlappedPtr, LlvmValueHandle bytesSentSlot, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle wsabufType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle wsabufStorage = LlvmApi.BuildAlloca(builder, wsabufType, name + "_wsabuf_storage");
        LlvmValueHandle wsabufPtr = GetArrayElementPointer(state, wsabufType, wsabufStorage, LlvmApi.ConstInt(state.I64, 0, 0), name + "_wsabuf_ptr");
        LlvmTypeHandle wsaSendType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I32, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle wsaSendPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWsaSendImport,
            name + "_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, len, state.I32, name + "_len_i32"), LlvmApi.BuildBitCast(builder, wsabufPtr, state.I32Ptr, name + "_len_ptr"));
        LlvmApi.BuildStore(builder, LlvmApi.BuildPtrToInt(builder, buffer, state.I64, name + "_buffer_addr"), LlvmApi.BuildBitCast(builder,
            LlvmApi.BuildGEP2(builder, state.I8, wsabufPtr, [LlvmApi.ConstInt(state.I64, 8, 0)], name + "_buffer_byte"),
            state.I64Ptr,
            name + "_buffer_ptr"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesSentSlot);
        return LlvmApi.BuildCall2(builder,
            wsaSendType,
            wsaSendPtr,
            [
                socket,
                LlvmApi.BuildBitCast(builder, wsabufPtr, state.I8Ptr, name + "_wsabuf_i8"),
                LlvmApi.ConstInt(state.I32, 1, 0),
                bytesSentSlot,
                LlvmApi.ConstInt(state.I32, 0, 0),
                overlappedPtr,
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_completion")
            ],
            name);
    }

    private static LlvmValueHandle EmitWindowsIssueWsaRecv(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle buffer, LlvmValueHandle len, LlvmValueHandle overlappedPtr, LlvmValueHandle bytesReceivedSlot, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle wsabufType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle wsabufStorage = LlvmApi.BuildAlloca(builder, wsabufType, name + "_wsabuf_storage");
        LlvmValueHandle wsabufPtr = GetArrayElementPointer(state, wsabufType, wsabufStorage, LlvmApi.ConstInt(state.I64, 0, 0), name + "_wsabuf_ptr");
        LlvmValueHandle flagsSlot = LlvmApi.BuildAlloca(builder, state.I32, name + "_flags_slot");
        LlvmTypeHandle wsaRecvType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I32Ptr, state.I8Ptr, state.I8Ptr]);
        LlvmValueHandle wsaRecvPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWsaRecvImport,
            name + "_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, len, state.I32, name + "_len_i32"), LlvmApi.BuildBitCast(builder, wsabufPtr, state.I32Ptr, name + "_len_ptr"));
        LlvmApi.BuildStore(builder, LlvmApi.BuildPtrToInt(builder, buffer, state.I64, name + "_buffer_addr"), LlvmApi.BuildBitCast(builder,
            LlvmApi.BuildGEP2(builder, state.I8, wsabufPtr, [LlvmApi.ConstInt(state.I64, 8, 0)], name + "_buffer_byte"),
            state.I64Ptr,
            name + "_buffer_ptr"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), flagsSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesReceivedSlot);
        return LlvmApi.BuildCall2(builder,
            wsaRecvType,
            wsaRecvPtr,
            [
                socket,
                LlvmApi.BuildBitCast(builder, wsabufPtr, state.I8Ptr, name + "_wsabuf_i8"),
                LlvmApi.ConstInt(state.I32, 1, 0),
                bytesReceivedSlot,
                flagsSlot,
                overlappedPtr,
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_completion")
            ],
            name);
    }

    private static LlvmValueHandle EmitWindowsWaitForIocpCompletion(LlvmCodegenState state, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle port = EmitWindowsEnsureIocpPort(state, name + "_ensure_port");
        LlvmValueHandle operationContextSlot = LlvmApi.BuildAlloca(builder, state.I64, name + "_operation_context_slot");
        LlvmValueHandle bytesSlot = LlvmApi.BuildAlloca(builder, state.I32, name + "_bytes_slot");
        LlvmValueHandle completionKeySlot = LlvmApi.BuildAlloca(builder, state.I64, name + "_completion_key_slot");
        LlvmValueHandle overlappedSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, name + "_overlapped_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), operationContextSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), completionKeySlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_null_overlapped"), overlappedSlot);

        LlvmTypeHandle getQueuedCompletionStatusType = LlvmApi.FunctionType(state.I32, [state.I64, state.I32Ptr, state.I64Ptr, LlvmApi.PointerTypeInContext(state.Target.Context, 0), state.I32]);
        LlvmValueHandle getQueuedCompletionStatusPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsGetQueuedCompletionStatusImport,
            name + "_ptr");
        LlvmValueHandle waitResult = LlvmApi.BuildCall2(builder,
            getQueuedCompletionStatusType,
            getQueuedCompletionStatusPtr,
            [
                port,
                bytesSlot,
                completionKeySlot,
                overlappedSlot,
                LlvmApi.ConstInt(state.I32, unchecked((uint)(-1)), 1)
            ],
            name + "_call");

        LlvmValueHandle overlappedPtr = LlvmApi.BuildLoad2(builder, state.I8Ptr, overlappedSlot, name + "_overlapped_value");
        LlvmValueHandle overlappedValue = LlvmApi.BuildPtrToInt(builder, overlappedPtr, state.I64, name + "_overlapped_i64");
        LlvmValueHandle hasOverlapped = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, overlappedValue, LlvmApi.ConstInt(state.I64, 0, 0), name + "_has_overlapped");
        LlvmBasicBlockHandle updateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_update");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_done");
        LlvmApi.BuildCondBr(builder, hasOverlapped, updateBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, updateBlock);
        LlvmValueHandle operationContext = LlvmApi.BuildSub(builder, overlappedValue, LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.Overlapped, 0), name + "_context_addr");
        LlvmApi.BuildStore(builder, operationContext, operationContextSlot);
        LlvmValueHandle completedState = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, waitResult, LlvmApi.ConstInt(state.I32, 0, 0), name + "_success"),
            LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StateCompleted, 0),
            LlvmApi.ConstInt(state.I64, WindowsIocpOperationLayout.StateFailed, 0),
            name + "_state_value");
        StoreMemory(state, operationContext, WindowsIocpOperationLayout.Status, completedState, name + "_store_status");
        StoreMemory(state, operationContext, WindowsIocpOperationLayout.BytesTransferred, LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesSlot, name + "_bytes_value"), state.I64, name + "_bytes_i64"), name + "_store_bytes");
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, operationContextSlot, name + "_operation_context");
    }

    private static LlvmValueHandle EmitWindowsCreateFile(LlvmCodegenState state, LlvmValueHandle pathCstr, int desiredAccess, int shareMode, int creationDisposition, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle createFileType = LlvmApi.FunctionType(state.I64, [state.I8Ptr, state.I32, state.I32, state.I8Ptr, state.I32, state.I32, state.I64]);
        LlvmValueHandle createFilePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsCreateFileImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            createFileType,
            createFilePtr,
            [
                pathCstr,
                LlvmApi.ConstInt(state.I32, unchecked((uint)desiredAccess), 1),
                LlvmApi.ConstInt(state.I32, unchecked((uint)shareMode), 0),
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_security"),
                LlvmApi.ConstInt(state.I32, unchecked((uint)creationDisposition), 0),
                LlvmApi.ConstInt(state.I32, 0x80, 0),
                LlvmApi.ConstInt(state.I64, 0, 0)
            ],
            name);
    }

    private static void EmitWindowsCloseHandle(LlvmCodegenState state, LlvmValueHandle handle, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle closeHandleType = LlvmApi.FunctionType(state.I32, [state.I64]);
        LlvmValueHandle closeHandlePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsCloseHandleImport,
            name + "_ptr");
        LlvmApi.BuildCall2(builder,
            closeHandleType,
            closeHandlePtr,
            [handle],
            name);
    }

    private static LlvmValueHandle EmitWindowsGetFileAttributes(LlvmCodegenState state, LlvmValueHandle pathCstr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle getFileAttributesType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle getFileAttributesPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsGetFileAttributesImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            getFileAttributesType,
            getFileAttributesPtr,
            [pathCstr],
            name);
    }

    private static LlvmValueHandle EmitWindowsReadFile(LlvmCodegenState state, LlvmValueHandle handle, LlvmValueHandle buffer, LlvmValueHandle len, LlvmValueHandle bytesReadSlot, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle readFileType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesReadSlot);
        LlvmValueHandle readFilePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsReadFileImport,
            name + "_ptr");
        LlvmValueHandle callResult = LlvmApi.BuildCall2(builder,
            readFileType,
            readFilePtr,
            [
                handle,
                buffer,
                len,
                bytesReadSlot,
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_overlapped")
            ],
            name);
        return LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, callResult, LlvmApi.ConstInt(state.I32, 0, 0), name + "_success");
    }

    private static LlvmValueHandle EmitWindowsWriteFile(LlvmCodegenState state, LlvmValueHandle handle, LlvmValueHandle buffer, LlvmValueHandle len, LlvmValueHandle bytesWrittenSlot, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle writeFileType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesWrittenSlot);
        LlvmValueHandle writeFilePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWriteFileImport,
            name + "_ptr");
        LlvmValueHandle callResult = LlvmApi.BuildCall2(builder,
            writeFileType,
            writeFilePtr,
            [
                handle,
                buffer,
                len,
                bytesWrittenSlot,
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_overlapped")
            ],
            name);
        return LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, callResult, LlvmApi.ConstInt(state.I32, 0, 0), name + "_success");
    }

    private static bool EmitPrintInt(LlvmCodegenState state, LlvmValueHandle value)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "print_idx");
        LlvmValueHandle workSlot = LlvmApi.BuildAlloca(builder, state.I64, "print_work");
        LlvmValueHandle negativeSlot = LlvmApi.BuildAlloca(builder, state.I64, "print_negative");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);

        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I8, 32);
        LlvmValueHandle buffer = LlvmApi.BuildAlloca(builder, bufferType, "print_buf");

        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle isNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, value, zero, "is_negative");
        LlvmValueHandle negativeValue = LlvmApi.BuildZExt(builder, isNegative, state.I64, "negative_i64");
        LlvmApi.BuildStore(builder, negativeValue, negativeSlot);
        LlvmValueHandle absValue = LlvmApi.BuildSelect(builder, isNegative, LlvmApi.BuildSub(builder, zero, value, "negated_value"), value, "abs_value");
        LlvmApi.BuildStore(builder, absValue, workSlot);

        var zeroBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_zero");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_loop_body");
        var maybeSignBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_maybe_sign");
        var signBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_sign");
        var writeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_write");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_continue");

        LlvmValueHandle isZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, absValue, zero, "is_zero");
        LlvmApi.BuildCondBr(builder, isZero, zeroBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, zeroBlock);
        StoreBufferByte(state, buffer, LlvmApi.ConstInt(state.I64, 31, 0), (byte)'0');
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), indexSlot);
        LlvmApi.BuildBr(builder, writeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle work = LlvmApi.BuildLoad2(builder, state.I64, workSlot, "work_value");
        LlvmValueHandle loopDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, work, zero, "loop_done");
        LlvmApi.BuildCondBr(builder, loopDone, maybeSignBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle digit = LlvmApi.BuildSRem(builder, work, LlvmApi.ConstInt(state.I64, 10, 0), "digit");
        LlvmValueHandle nextWork = LlvmApi.BuildSDiv(builder, work, LlvmApi.ConstInt(state.I64, 10, 0), "next_work");
        LlvmApi.BuildStore(builder, nextWork, workSlot);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "digit_idx");
        LlvmValueHandle writeIndex = LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), idx, "digit_write_index");
        LlvmValueHandle asciiDigit = LlvmApi.BuildAdd(builder, digit, LlvmApi.ConstInt(state.I64, (byte)'0', 0), "ascii_digit");
        StoreBufferByte(state, buffer, writeIndex, asciiDigit);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), "idx_inc"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, maybeSignBlock);
        LlvmValueHandle negative = LlvmApi.BuildLoad2(builder, state.I64, negativeSlot, "negative_value");
        LlvmValueHandle hasSign = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, negative, zero, "has_sign");
        LlvmApi.BuildCondBr(builder, hasSign, signBlock, writeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, signBlock);
        LlvmValueHandle idxBeforeSign = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "idx_before_sign");
        LlvmValueHandle signIndex = LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), idxBeforeSign, "sign_index");
        StoreBufferByte(state, buffer, signIndex, (byte)'-');
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idxBeforeSign, LlvmApi.ConstInt(state.I64, 1, 0), "idx_with_sign"), indexSlot);
        LlvmApi.BuildBr(builder, writeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, writeBlock);
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "print_count");
        LlvmValueHandle startIndex = LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 32, 0), count, "start_index");
        LlvmValueHandle dataPtr = GetArrayElementPointer(state, bufferType, buffer, startIndex, "print_data_ptr");
        EmitWriteBytes(state, dataPtr, count);
        EmitWriteBytes(state, EmitStackByteArray(state, [10]), LlvmApi.ConstInt(state.I64, 1, 0));
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return false;
    }

    private static void EmitWriteBytes(LlvmCodegenState state, LlvmValueHandle bytePtr, LlvmValueHandle len)
    {
        if (IsLinuxFlavor(state.Flavor))
        {
            EmitLinuxSyscall(
                state,
                SyscallWrite,
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.BuildPtrToInt(state.Target.Builder, bytePtr, state.I64, "write_ptr_i64"),
                len,
                "sys_write");
            return;
        }

        EmitWindowsWriteBytes(state, bytePtr, len);
    }

    private static LlvmValueHandle EmitWindowsGetStdHandle(LlvmCodegenState state, uint handleKind, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle getStdHandleType = LlvmApi.FunctionType(state.I64, [state.I32]);
        LlvmValueHandle getStdHandlePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsGetStdHandleImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            getStdHandleType,
            getStdHandlePtr,
            [LlvmApi.ConstInt(state.I32, handleKind, 1)],
            name);
    }

    private static LlvmValueHandle EmitWindowsReadByte(LlvmCodegenState state, LlvmValueHandle stdinHandle, LlvmValueHandle byteSlot, LlvmValueHandle bytesReadSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle readFileType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesReadSlot);
        LlvmValueHandle readFilePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsReadFileImport,
            "read_file_ptr");
        LlvmApi.BuildCall2(builder,
            readFileType,
            readFilePtr,
            [
                stdinHandle,
                byteSlot,
                LlvmApi.ConstInt(state.I32, 1, 0),
                bytesReadSlot,
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, "null_overlapped")
            ],
            "read_file");
        return LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesReadSlot, "read_line_bytes_read_value"), state.I64, "read_line_bytes_read_i64");
    }

    private static void EmitWindowsWriteBytes(LlvmCodegenState state, LlvmValueHandle bytePtr, LlvmValueHandle len)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle writeFileType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        LlvmValueHandle stdoutHandle = EmitWindowsGetStdHandle(state, StdOutputHandle, "stdout_handle");
        LlvmValueHandle bytesWritten = LlvmApi.BuildAlloca(builder, state.I32, "bytes_written");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesWritten);
        LlvmValueHandle writeFilePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWriteFileImport,
            "write_file_ptr");
        LlvmApi.BuildCall2(builder,
            writeFileType,
            writeFilePtr,
            [
                stdoutHandle,
                bytePtr,
                LlvmApi.BuildTrunc(builder, NormalizeToI64(state, len), state.I32, "write_len_i32"),
                bytesWritten,
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, "null_overlapped")
            ],
            "write_file");
    }

    private static LlvmValueHandle EmitLinuxSyscall(LlvmCodegenState state, long nr, LlvmValueHandle arg1, LlvmValueHandle arg2, LlvmValueHandle arg3, string name)
    {
        long resolved = ResolveSyscallNr(state.Flavor, nr);
        if (state.Flavor == LlvmCodegenFlavor.LinuxArm64)
        {
            // AArch64 Linux has no open() syscall — only openat(dirfd, path, flags, mode).
            // When the caller requests SyscallOpen, translate it to openat with AT_FDCWD (-100)
            // as the directory file descriptor, shifting the original arguments.
            if (nr == SyscallOpen)
            {
                return EmitSyscall4Arm64(state, resolved,
                    LlvmApi.ConstInt(state.I64, unchecked((ulong)(-100L)), 1), // AT_FDCWD
                    arg1, arg2, arg3, name);
            }

            return EmitSyscallArm64(state, resolved, arg1, arg2, arg3, name);
        }

        return EmitSyscallX86(state, resolved, arg1, arg2, arg3, name);
    }

    private static LlvmValueHandle EmitLinuxSyscall4(LlvmCodegenState state, long nr,
        LlvmValueHandle arg1, LlvmValueHandle arg2, LlvmValueHandle arg3, LlvmValueHandle arg4, string name)
    {
        long resolved = ResolveSyscallNr(state.Flavor, nr);
        if (state.Flavor == LlvmCodegenFlavor.LinuxArm64)
        {
            return EmitSyscall4Arm64(state, resolved, arg1, arg2, arg3, arg4, name);
        }

        return EmitSyscall6X86(
            state,
            resolved,
            arg1,
            arg2,
            arg3,
            arg4,
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            name);
    }

    private static LlvmValueHandle EmitSyscallX86(LlvmCodegenState state, long nr, LlvmValueHandle arg1, LlvmValueHandle arg2, LlvmValueHandle arg3, string name)
    {
        LlvmTypeHandle syscallType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64, state.I64, state.I64]);
        LlvmValueHandle syscall = LlvmApi.GetInlineAsm(
            syscallType,
            "syscall",
            "={rax},{rax},{rdi},{rsi},{rdx},~{rcx},~{r11},~{memory}",
            true,
            false);
        return LlvmApi.BuildCall2(state.Target.Builder,
            syscallType,
            syscall,
            [
                LlvmApi.ConstInt(state.I64, unchecked((ulong)nr), 1),
                NormalizeToI64(state, arg1),
                NormalizeToI64(state, arg2),
                NormalizeToI64(state, arg3)
            ],
            name);
    }

    private static LlvmValueHandle EmitSyscallArm64(LlvmCodegenState state, long nr, LlvmValueHandle arg1, LlvmValueHandle arg2, LlvmValueHandle arg3, string name)
    {
        // AArch64 Linux syscall convention:
        //   x8 = syscall number
        //   x0 = arg1, x1 = arg2, x2 = arg3
        //   svc #0 to invoke
        //   result in x0
        LlvmTypeHandle syscallType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64, state.I64, state.I64]);
        LlvmValueHandle syscall = LlvmApi.GetInlineAsm(
            syscallType,
            "svc #0",
            "={x0},{x8},{x0},{x1},{x2},~{memory},~{cc}",
            true,
            false);
        return LlvmApi.BuildCall2(state.Target.Builder,
            syscallType,
            syscall,
            [
                LlvmApi.ConstInt(state.I64, unchecked((ulong)nr), 1),
                NormalizeToI64(state, arg1),
                NormalizeToI64(state, arg2),
                NormalizeToI64(state, arg3)
            ],
            name);
    }

    private static LlvmValueHandle EmitSyscall4Arm64(LlvmCodegenState state, long nr, LlvmValueHandle arg1, LlvmValueHandle arg2, LlvmValueHandle arg3, LlvmValueHandle arg4, string name)
    {
        // AArch64 Linux 4-argument syscall (e.g., openat).
        LlvmTypeHandle syscallType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64, state.I64, state.I64, state.I64]);
        LlvmValueHandle syscall = LlvmApi.GetInlineAsm(
            syscallType,
            "svc #0",
            "={x0},{x8},{x0},{x1},{x2},{x3},~{memory},~{cc}",
            true,
            false);
        return LlvmApi.BuildCall2(state.Target.Builder,
            syscallType,
            syscall,
            [
                LlvmApi.ConstInt(state.I64, unchecked((ulong)nr), 1),
                NormalizeToI64(state, arg1),
                NormalizeToI64(state, arg2),
                NormalizeToI64(state, arg3),
                NormalizeToI64(state, arg4)
            ],
            name);
    }

    private static LlvmValueHandle EmitLinuxSyscall6(LlvmCodegenState state, long nr,
        LlvmValueHandle arg1, LlvmValueHandle arg2, LlvmValueHandle arg3,
        LlvmValueHandle arg4, LlvmValueHandle arg5, LlvmValueHandle arg6, string name)
    {
        long resolved = ResolveSyscallNr(state.Flavor, nr);
        if (state.Flavor == LlvmCodegenFlavor.LinuxArm64)
        {
            return EmitSyscall6Arm64(state, resolved, arg1, arg2, arg3, arg4, arg5, arg6, name);
        }

        return EmitSyscall6X86(state, resolved, arg1, arg2, arg3, arg4, arg5, arg6, name);
    }

    private static LlvmValueHandle EmitSyscall6X86(LlvmCodegenState state, long nr,
        LlvmValueHandle arg1, LlvmValueHandle arg2, LlvmValueHandle arg3,
        LlvmValueHandle arg4, LlvmValueHandle arg5, LlvmValueHandle arg6, string name)
    {
        // x86-64 Linux 6-argument syscall convention:
        //   rax = syscall number
        //   rdi = arg1, rsi = arg2, rdx = arg3, r10 = arg4, r8 = arg5, r9 = arg6
        //   Note: r10 is used instead of rcx (rcx is clobbered by syscall).
        LlvmTypeHandle syscallType = LlvmApi.FunctionType(state.I64,
            [state.I64, state.I64, state.I64, state.I64, state.I64, state.I64, state.I64]);
        LlvmValueHandle syscall = LlvmApi.GetInlineAsm(
            syscallType,
            "syscall",
            "={rax},{rax},{rdi},{rsi},{rdx},{r10},{r8},{r9},~{rcx},~{r11},~{memory}",
            true,
            false);
        return LlvmApi.BuildCall2(state.Target.Builder,
            syscallType,
            syscall,
            [
                LlvmApi.ConstInt(state.I64, unchecked((ulong)nr), 1),
                NormalizeToI64(state, arg1),
                NormalizeToI64(state, arg2),
                NormalizeToI64(state, arg3),
                NormalizeToI64(state, arg4),
                NormalizeToI64(state, arg5),
                NormalizeToI64(state, arg6)
            ],
            name);
    }

    private static LlvmValueHandle EmitSyscall6Arm64(LlvmCodegenState state, long nr,
        LlvmValueHandle arg1, LlvmValueHandle arg2, LlvmValueHandle arg3,
        LlvmValueHandle arg4, LlvmValueHandle arg5, LlvmValueHandle arg6, string name)
    {
        // AArch64 Linux 6-argument syscall:
        //   x8 = syscall number
        //   x0 = arg1, x1 = arg2, x2 = arg3, x3 = arg4, x4 = arg5, x5 = arg6
        LlvmTypeHandle syscallType = LlvmApi.FunctionType(state.I64,
            [state.I64, state.I64, state.I64, state.I64, state.I64, state.I64, state.I64]);
        LlvmValueHandle syscall = LlvmApi.GetInlineAsm(
            syscallType,
            "svc #0",
            "={x0},{x8},{x0},{x1},{x2},{x3},{x4},{x5},~{memory},~{cc}",
            true,
            false);
        return LlvmApi.BuildCall2(state.Target.Builder,
            syscallType,
            syscall,
            [
                LlvmApi.ConstInt(state.I64, unchecked((ulong)nr), 1),
                NormalizeToI64(state, arg1),
                NormalizeToI64(state, arg2),
                NormalizeToI64(state, arg3),
                NormalizeToI64(state, arg4),
                NormalizeToI64(state, arg5),
                NormalizeToI64(state, arg6)
            ],
            name);
    }
}
