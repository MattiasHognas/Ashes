using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{

    private static LlvmValueHandle EmitRustlsResultIsOk(LlvmCodegenState state, LlvmValueHandle result, string name)
        => LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Eq, result, LlvmApi.ConstInt(state.I32, RustlsResultOk, 0), name);

    private static LlvmValueHandle EmitRustlsResultIsPlaintextEmpty(LlvmCodegenState state, LlvmValueHandle result, string name)
        => LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Eq, result, LlvmApi.ConstInt(state.I32, RustlsResultPlaintextEmpty, 0), name);

    private static LlvmValueHandle EmitRustlsIoResultIsWouldBlock(LlvmCodegenState state, LlvmValueHandle ioResult, string name)
        => LlvmApi.BuildICmp(
            state.Target.Builder,
            LlvmIntPredicate.Eq,
            ioResult,
            state.Flavor == LlvmCodegenFlavor.WindowsX64
                ? LlvmApi.ConstInt(state.I32, WindowsWsaErrorWouldBlock, 0)
                : LlvmApi.ConstInt(state.I32, unchecked((ulong)(-LinuxErrWouldBlock)), 0),
            name);

    private static LlvmValueHandle EmitRustlsClientConnectionNew(
        LlvmCodegenState state,
        LlvmValueHandle libsslHandle,
        LlvmValueHandle configHandle,
        LlvmValueHandle serverNameCstr,
        LlvmValueHandle outConnectionSlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle opaquePtr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, opaquePtr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_client_connection_new", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [
                LlvmApi.BuildIntToPtr(builder, configHandle, state.I8Ptr, name + "_config_ptr"),
                serverNameCstr,
                LlvmApi.BuildBitCast(builder, outConnectionSlot, opaquePtr, name + "_out_connection")
            ],
            name);
    }

    // rustls_certified_key_build(cert_chain_pem, len, private_key_pem, len, out) -> rustls_result
    private static LlvmValueHandle EmitRustlsCertifiedKeyBuild(
        LlvmCodegenState state,
        LlvmValueHandle libsslHandle,
        LlvmValueHandle certPtr,
        LlvmValueHandle certLen,
        LlvmValueHandle keyPtr,
        LlvmValueHandle keyLen,
        LlvmValueHandle outKeySlot,
        string name)
    {
        LlvmTypeHandle opaquePtr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I64, state.I8Ptr, state.I64, opaquePtr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_certified_key_build", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [certPtr, certLen, keyPtr, keyLen, LlvmApi.BuildBitCast(state.Target.Builder, outKeySlot, opaquePtr, name + "_out_key")],
            name);
    }

    // rustls_server_config_builder_new() -> *builder (safe defaults: default provider + versions)
    private static LlvmValueHandle EmitRustlsServerConfigBuilderNew(LlvmCodegenState state, LlvmValueHandle libsslHandle, string name)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8Ptr, []);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_server_config_builder_new", name + "_resolve");
        LlvmValueHandle builderPtr = EmitCallFunctionAddress(state, functionAddress, functionType, [], name);
        return LlvmApi.BuildPtrToInt(state.Target.Builder, builderPtr, state.I64, name + "_handle");
    }

    // rustls_server_config_builder_set_certified_keys(builder, keys**, count) -> rustls_result
    private static LlvmValueHandle EmitRustlsServerConfigBuilderSetCertifiedKeys(
        LlvmCodegenState state,
        LlvmValueHandle libsslHandle,
        LlvmValueHandle builderHandle,
        LlvmValueHandle keysArraySlot,
        LlvmValueHandle count,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle opaquePtr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, opaquePtr, state.I64]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_server_config_builder_set_certified_keys", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [
                LlvmApi.BuildIntToPtr(builder, builderHandle, state.I8Ptr, name + "_builder_ptr"),
                LlvmApi.BuildBitCast(builder, keysArraySlot, opaquePtr, name + "_keys_ptr"),
                count
            ],
            name);
    }

    // rustls_server_config_builder_build(builder, out_config**) -> rustls_result
    private static LlvmValueHandle EmitRustlsServerConfigBuilderBuild(
        LlvmCodegenState state,
        LlvmValueHandle libsslHandle,
        LlvmValueHandle builderHandle,
        LlvmValueHandle outConfigSlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle opaquePtr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, opaquePtr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_server_config_builder_build", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [
                LlvmApi.BuildIntToPtr(builder, builderHandle, state.I8Ptr, name + "_builder_ptr"),
                LlvmApi.BuildBitCast(builder, outConfigSlot, opaquePtr, name + "_out_config")
            ],
            name);
    }

    // rustls_server_connection_new(config, out_connection**) -> rustls_result
    private static LlvmValueHandle EmitRustlsServerConnectionNew(
        LlvmCodegenState state,
        LlvmValueHandle libsslHandle,
        LlvmValueHandle configHandle,
        LlvmValueHandle outConnectionSlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle opaquePtr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, opaquePtr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_server_connection_new", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [
                LlvmApi.BuildIntToPtr(builder, configHandle, state.I8Ptr, name + "_config_ptr"),
                LlvmApi.BuildBitCast(builder, outConnectionSlot, opaquePtr, name + "_out_connection")
            ],
            name);
    }

    private static LlvmValueHandle EmitRustlsConnectionWantsRead(LlvmCodegenState state, LlvmValueHandle libsslHandle, LlvmValueHandle connectionHandle, string name)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8, [state.I8Ptr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_connection_wants_read", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [LlvmApi.BuildIntToPtr(state.Target.Builder, connectionHandle, state.I8Ptr, name + "_connection_ptr")],
            name);
    }

    private static LlvmValueHandle EmitRustlsConnectionWantsWrite(LlvmCodegenState state, LlvmValueHandle libsslHandle, LlvmValueHandle connectionHandle, string name)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8, [state.I8Ptr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_connection_wants_write", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [LlvmApi.BuildIntToPtr(state.Target.Builder, connectionHandle, state.I8Ptr, name + "_connection_ptr")],
            name);
    }

    private static LlvmValueHandle EmitRustlsConnectionIsHandshaking(LlvmCodegenState state, LlvmValueHandle libsslHandle, LlvmValueHandle connectionHandle, string name)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8, [state.I8Ptr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_connection_is_handshaking", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [LlvmApi.BuildIntToPtr(state.Target.Builder, connectionHandle, state.I8Ptr, name + "_connection_ptr")],
            name);
    }

    private static LlvmValueHandle EmitRustlsConnectionProcessNewPackets(LlvmCodegenState state, LlvmValueHandle libsslHandle, LlvmValueHandle connectionHandle, string name)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_connection_process_new_packets", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [LlvmApi.BuildIntToPtr(state.Target.Builder, connectionHandle, state.I8Ptr, name + "_connection_ptr")],
            name);
    }

    private static LlvmValueHandle EmitRustlsConnectionReadTls(
        LlvmCodegenState state,
        LinuxTlsGlobals globals,
        LlvmValueHandle libsslHandle,
        LlvmValueHandle connectionHandle,
        LlvmValueHandle socket,
        LlvmValueHandle outBytesSlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle opaquePtr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, opaquePtr, state.I8Ptr, state.I64Ptr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_connection_read_tls", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [
                LlvmApi.BuildIntToPtr(builder, connectionHandle, state.I8Ptr, name + "_connection_ptr"),
                LlvmApi.BuildBitCast(builder, globals.RustlsReadCallback, opaquePtr, name + "_callback_ptr"),
                LlvmApi.BuildIntToPtr(builder, socket, state.I8Ptr, name + "_socket_userdata"),
                outBytesSlot
            ],
            name);
    }

    private static LlvmValueHandle EmitRustlsConnectionWriteTls(
        LlvmCodegenState state,
        LinuxTlsGlobals globals,
        LlvmValueHandle libsslHandle,
        LlvmValueHandle connectionHandle,
        LlvmValueHandle socket,
        LlvmValueHandle outBytesSlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle opaquePtr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, opaquePtr, state.I8Ptr, state.I64Ptr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_connection_write_tls", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [
                LlvmApi.BuildIntToPtr(builder, connectionHandle, state.I8Ptr, name + "_connection_ptr"),
                LlvmApi.BuildBitCast(builder, globals.RustlsWriteCallback, opaquePtr, name + "_callback_ptr"),
                LlvmApi.BuildIntToPtr(builder, socket, state.I8Ptr, name + "_socket_userdata"),
                outBytesSlot
            ],
            name);
    }

    private static LlvmValueHandle EmitRustlsConnectionWrite(
        LlvmCodegenState state,
        LlvmValueHandle libsslHandle,
        LlvmValueHandle connectionHandle,
        LlvmValueHandle bufferPtr,
        LlvmValueHandle byteCount,
        LlvmValueHandle outBytesSlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I64, state.I64Ptr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_connection_write", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [
                LlvmApi.BuildIntToPtr(builder, connectionHandle, state.I8Ptr, name + "_connection_ptr"),
                bufferPtr,
                byteCount,
                outBytesSlot
            ],
            name);
    }

    private static LlvmValueHandle EmitRustlsConnectionRead(
        LlvmCodegenState state,
        LlvmValueHandle libsslHandle,
        LlvmValueHandle connectionHandle,
        LlvmValueHandle bufferPtr,
        LlvmValueHandle byteCount,
        LlvmValueHandle outBytesSlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I64, state.I64Ptr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_connection_read", name + "_resolve");
        return EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [
                LlvmApi.BuildIntToPtr(builder, connectionHandle, state.I8Ptr, name + "_connection_ptr"),
                bufferPtr,
                byteCount,
                outBytesSlot
            ],
            name);
    }

    private static void EmitRustlsConnectionSendCloseNotify(LlvmCodegenState state, LlvmValueHandle libsslHandle, LlvmValueHandle connectionHandle, string name)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_connection_send_close_notify", name + "_resolve");
        _ = EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [LlvmApi.BuildIntToPtr(state.Target.Builder, connectionHandle, state.I8Ptr, name + "_connection_ptr")],
            string.Empty);
    }

    private static void EmitRustlsConnectionFree(LlvmCodegenState state, LlvmValueHandle libsslHandle, LlvmValueHandle connectionHandle, string name)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_connection_free", name + "_resolve");
        _ = EmitCallFunctionAddress(state,
            functionAddress,
            functionType,
            [LlvmApi.BuildIntToPtr(state.Target.Builder, connectionHandle, state.I8Ptr, name + "_connection_ptr")],
            string.Empty);
    }

    private static LlvmValueHandle EmitRustlsErrorString(LlvmCodegenState state, LlvmValueHandle libsslHandle, LlvmValueHandle resultCode, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I8, 256);
        LlvmValueHandle buffer = LlvmApi.BuildAlloca(builder, bufferType, prefix + "_buffer");
        LlvmValueHandle bufferPtr = GetArrayElementPointer(state, bufferType, buffer, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_buffer_ptr");
        LlvmValueHandle outLengthSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_out_length_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), outLengthSlot);

        LlvmTypeHandle functionType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I32, state.I8Ptr, state.I64, state.I64Ptr]);
        LlvmValueHandle functionAddress = EmitTlsResolveSymbol(state, libsslHandle, "rustls_error", prefix + "_resolve");
        _ = EmitCallFunctionAddress(
            state,
            functionAddress,
            functionType,
            [
                resultCode,
                bufferPtr,
                LlvmApi.ConstInt(state.I64, 256, 0),
                outLengthSlot
            ],
            string.Empty);

        return EmitHeapStringSliceFromBytesPointer(
            state,
            bufferPtr,
            LlvmApi.BuildLoad2(builder, state.I64, outLengthSlot, prefix + "_out_length"),
            prefix + "_message");
    }

    private static LlvmValueHandle EmitLoadI32AtOffset(LlvmCodegenState state, LlvmValueHandle baseAddress, int offset, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle basePtr = LlvmApi.BuildIntToPtr(builder, baseAddress, state.I8Ptr, name + "_base_ptr");
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(builder, state.I8, basePtr, [LlvmApi.ConstInt(state.I64, unchecked((ulong)offset), 0)], name + "_byte_ptr");
        LlvmValueHandle i32Ptr = LlvmApi.BuildBitCast(builder, bytePtr, state.I32Ptr, name + "_i32_ptr");
        return LlvmApi.BuildLoad2(builder, state.I32, i32Ptr, name);
    }

    private static LlvmValueHandle EmitCStringToHeapString(LlvmCodegenState state, LlvmValueHandle cstrPtr, string prefix)
    {
        LlvmValueHandle len = EmitLinuxStrlen(state, cstrPtr, prefix + "_strlen");
        return EmitHeapStringSliceFromBytesPointer(state, cstrPtr, len, prefix + "_string");
    }

    private static LlvmValueHandle EmitEnsureLinuxTlsRuntimeInitialized(LlvmCodegenState state, LinuxTlsGlobals globals, HermeticTlsRuntimeAsset? rustlsSharedLibrary, LlvmValueHandle linkedTlsPayloadStartGlobal, LlvmValueHandle linkedTlsPayloadEndGlobal, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle initBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_init");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");

        LlvmValueHandle currentStatus = LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_current_status");
        LlvmValueHandle needsInit = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_needs_init");
        LlvmApi.BuildCondBr(builder, needsInit, initBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initBlock);
        if (rustlsSharedLibrary is null)
        {
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), globals.InitStatusGlobal);
            LlvmApi.BuildBr(builder, doneBlock);
            LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
            return LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_status");
        }

        LlvmBasicBlockHandle afterWriteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_write");
        LlvmBasicBlockHandle resolveSymbolsBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_resolve_symbols");
        LlvmBasicBlockHandle initializeConfigBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_initialize_config");
        LlvmBasicBlockHandle checkCertFileLengthBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_cert_file_length");
        LlvmBasicBlockHandle createPemRootStoreBuilderBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_pem_root_store_builder");
        LlvmBasicBlockHandle buildPemRootStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build_pem_root_store");
        LlvmBasicBlockHandle createPemVerifierBuilderBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_pem_verifier_builder");
        LlvmBasicBlockHandle buildPemVerifierBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build_pem_verifier");
        LlvmBasicBlockHandle createPlatformVerifierBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_platform_verifier");
        LlvmBasicBlockHandle createBuilderBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_builder");
        LlvmBasicBlockHandle attachVerifierBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_attach_verifier");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmBasicBlockHandle failMissingLibraryBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail_missing_library");
        LlvmBasicBlockHandle failMissingSymbolBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail_missing_symbol");
        LlvmBasicBlockHandle failInitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail_init");

        LlvmValueHandle payloadPid = EmitLinuxGetPid(state, prefix + "_payload_pid");
        LlvmValueHandle payloadPidText = EmitNonNegativeIntToString(
            state,
            LlvmApi.BuildZExt(builder, payloadPid, state.I64, prefix + "_payload_pid_i64"),
            prefix + "_payload_pid_text");
        LlvmValueHandle payloadPathRef = EmitStringConcat(state, EmitHeapStringLiteral(state, "/tmp/ashes-tls-"), payloadPidText);
        payloadPathRef = EmitStringConcat(state, payloadPathRef, EmitHeapStringLiteral(state, "-"));
        payloadPathRef = EmitStringConcat(state, payloadPathRef, EmitHeapStringLiteral(state, rustlsSharedLibrary.EmbeddedFileName));
        LlvmValueHandle payloadPathCstr = EmitStringToCString(state, payloadPathRef, prefix + "_payload_path");
        LlvmValueHandle payloadStartAddress = LlvmApi.BuildPtrToInt(builder, linkedTlsPayloadStartGlobal, state.I64, prefix + "_payload_start");
        LlvmValueHandle payloadEndAddress = LlvmApi.BuildPtrToInt(builder, linkedTlsPayloadEndGlobal, state.I64, prefix + "_payload_end");
        LlvmValueHandle payloadLength = LlvmApi.BuildSub(builder, payloadEndAddress, payloadStartAddress, prefix + "_payload_length");
        LlvmValueHandle payloadWriteResult = EmitLinuxFileWriteBytes(
            state,
            payloadPathRef,
            payloadStartAddress,
            payloadLength);
        LlvmValueHandle payloadWriteOk = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Eq,
            LoadMemory(state, payloadWriteResult, 0, prefix + "_payload_write_tag"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_payload_write_ok");
        LlvmApi.BuildCondBr(builder, payloadWriteOk, afterWriteBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterWriteBlock);
        LlvmValueHandle libsslHandle = EmitLinuxDlopen(state, payloadPathCstr, prefix + "_dlopen_rustls");
        LlvmApi.BuildStore(builder, libsslHandle, globals.LibsslHandleGlobal);
        LlvmValueHandle hasLibssl = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, libsslHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_rustls");
        LlvmApi.BuildCondBr(builder, hasLibssl, resolveSymbolsBlock, failMissingLibraryBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resolveSymbolsBlock);
        LlvmValueHandle platformVerifierFn = EmitLinuxDlsym(state, libsslHandle, "rustls_platform_server_cert_verifier", prefix + "_resolve_platform_verifier");
        LlvmValueHandle configBuilderNewFn = EmitLinuxDlsym(state, libsslHandle, "rustls_client_config_builder_new", prefix + "_resolve_builder_new");
        LlvmValueHandle configBuilderSetVerifierFn = EmitLinuxDlsym(state, libsslHandle, "rustls_client_config_builder_set_server_verifier", prefix + "_resolve_set_verifier");
        LlvmValueHandle configBuilderBuildFn = EmitLinuxDlsym(state, libsslHandle, "rustls_client_config_builder_build", prefix + "_resolve_build");
        LlvmValueHandle verifierFreeFn = EmitLinuxDlsym(state, libsslHandle, "rustls_server_cert_verifier_free", prefix + "_resolve_verifier_free");
        LlvmValueHandle rootStoreBuilderNewFn = EmitLinuxDlsym(state, libsslHandle, "rustls_root_cert_store_builder_new", prefix + "_resolve_root_store_builder_new");
        LlvmValueHandle rootStoreBuilderLoadRootsFn = EmitLinuxDlsym(state, libsslHandle, "rustls_root_cert_store_builder_load_roots_from_file", prefix + "_resolve_root_store_builder_load_roots");
        LlvmValueHandle rootStoreBuilderBuildFn = EmitLinuxDlsym(state, libsslHandle, "rustls_root_cert_store_builder_build", prefix + "_resolve_root_store_builder_build");
        LlvmValueHandle rootStoreBuilderFreeFn = EmitLinuxDlsym(state, libsslHandle, "rustls_root_cert_store_builder_free", prefix + "_resolve_root_store_builder_free");
        LlvmValueHandle rootStoreFreeFn = EmitLinuxDlsym(state, libsslHandle, "rustls_root_cert_store_free", prefix + "_resolve_root_store_free");
        LlvmValueHandle pemVerifierBuilderNewFn = EmitLinuxDlsym(state, libsslHandle, "rustls_web_pki_server_cert_verifier_builder_new", prefix + "_resolve_pem_verifier_builder_new");
        LlvmValueHandle pemVerifierBuilderBuildFn = EmitLinuxDlsym(state, libsslHandle, "rustls_web_pki_server_cert_verifier_builder_build", prefix + "_resolve_pem_verifier_builder_build");
        LlvmValueHandle pemVerifierBuilderFreeFn = EmitLinuxDlsym(state, libsslHandle, "rustls_web_pki_server_cert_verifier_builder_free", prefix + "_resolve_pem_verifier_builder_free");
        LlvmValueHandle haveAllSymbols = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, platformVerifierFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_platform_verifier"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, configBuilderNewFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_builder_new"),
            prefix + "_have_first_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, configBuilderSetVerifierFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_set_verifier"),
            prefix + "_have_second_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, configBuilderBuildFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_build"),
            prefix + "_have_third_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, verifierFreeFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_verifier_free"),
            prefix + "_have_fourth_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, rootStoreBuilderNewFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_root_store_builder_new"),
            prefix + "_have_fifth_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, rootStoreBuilderLoadRootsFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_root_store_builder_load_roots"),
            prefix + "_have_sixth_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, rootStoreBuilderBuildFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_root_store_builder_build"),
            prefix + "_have_seventh_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, rootStoreBuilderFreeFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_root_store_builder_free"),
            prefix + "_have_eighth_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, rootStoreFreeFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_root_store_free"),
            prefix + "_have_ninth_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, pemVerifierBuilderNewFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_pem_verifier_builder_new"),
            prefix + "_have_tenth_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, pemVerifierBuilderBuildFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_pem_verifier_builder_build"),
            prefix + "_have_eleventh_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, pemVerifierBuilderFreeFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_pem_verifier_builder_free"),
            prefix + "_have_all_symbols");
        LlvmApi.BuildCondBr(builder, haveAllSymbols, initializeConfigBlock, failMissingSymbolBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initializeConfigBlock);
        LlvmValueHandle verifierSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, prefix + "_verifier_slot");
        LlvmValueHandle configSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, prefix + "_config_slot");
        LlvmValueHandle rootStoreBuilderSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, prefix + "_root_store_builder_slot");
        LlvmValueHandle rootStoreSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, prefix + "_root_store_slot");
        LlvmValueHandle pemVerifierBuilderSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, prefix + "_pem_verifier_builder_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), verifierSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), configSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), rootStoreBuilderSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), rootStoreSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), pemVerifierBuilderSlot);
        LlvmValueHandle certFilePath = EmitLinuxGetEnv(state, "SSL_CERT_FILE", prefix + "_get_ssl_cert_file");
        LlvmValueHandle hasCertFile = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LlvmApi.BuildPtrToInt(builder, certFilePath, state.I64, prefix + "_ssl_cert_file_i64"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_has_ssl_cert_file");
        LlvmApi.BuildCondBr(builder, hasCertFile, checkCertFileLengthBlock, createPlatformVerifierBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkCertFileLengthBlock);
        LlvmValueHandle certFileLength = EmitLinuxStrlen(state, certFilePath, prefix + "_ssl_cert_file_length");
        LlvmValueHandle hasNonEmptyCertFile = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            certFileLength,
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_has_non_empty_ssl_cert_file");
        LlvmApi.BuildCondBr(builder, hasNonEmptyCertFile, createPemRootStoreBuilderBlock, createPlatformVerifierBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createPemRootStoreBuilderBlock);
        LlvmTypeHandle rootStoreBuilderNewType = LlvmApi.FunctionType(state.I8Ptr, []);
        LlvmValueHandle rootStoreBuilderHandle = EmitCallFunctionAddress(
            state,
            rootStoreBuilderNewFn,
            rootStoreBuilderNewType,
            Array.Empty<LlvmValueHandle>(),
            prefix + "_root_store_builder_new_call");
        LlvmApi.BuildStore(builder, rootStoreBuilderHandle, rootStoreBuilderSlot);
        LlvmValueHandle haveRootStoreBuilder = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LlvmApi.BuildPtrToInt(builder, rootStoreBuilderHandle, state.I64, prefix + "_root_store_builder_i64"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_have_root_store_builder");
        LlvmApi.BuildCondBr(builder, haveRootStoreBuilder, buildPemRootStoreBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildPemRootStoreBlock);
        LlvmValueHandle rootStoreBuilderHandleValue = LlvmApi.BuildLoad2(builder, state.I8Ptr, rootStoreBuilderSlot, prefix + "_root_store_builder_handle");
        LlvmTypeHandle rootStoreBuilderLoadRootsType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I8]);
        LlvmValueHandle loadRootsStatus = EmitCallFunctionAddress(
            state,
            rootStoreBuilderLoadRootsFn,
            rootStoreBuilderLoadRootsType,
            [rootStoreBuilderHandleValue, certFilePath, LlvmApi.ConstInt(state.I8, 1, 0)],
            prefix + "_root_store_builder_load_roots_call");
        LlvmTypeHandle rootStoreBuilderBuildType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, LlvmApi.PointerTypeInContext(state.Target.Context, 0)]);
        LlvmValueHandle buildRootStoreStatus = EmitCallFunctionAddress(
            state,
            rootStoreBuilderBuildFn,
            rootStoreBuilderBuildType,
            [rootStoreBuilderHandleValue, rootStoreSlot],
            prefix + "_root_store_builder_build_call");
        LlvmTypeHandle rootStoreBuilderFreeType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        _ = EmitCallFunctionAddress(state, rootStoreBuilderFreeFn, rootStoreBuilderFreeType, [rootStoreBuilderHandleValue], string.Empty);
        LlvmValueHandle rootStoreHandle = LlvmApi.BuildLoad2(builder, state.I8Ptr, rootStoreSlot, prefix + "_root_store_handle");
        LlvmValueHandle rootStoreOk = LlvmApi.BuildAnd(builder,
            EmitRustlsResultIsOk(state, loadRootsStatus, prefix + "_load_roots_status_ok"),
            EmitRustlsResultIsOk(state, buildRootStoreStatus, prefix + "_build_root_store_status_ok"),
            prefix + "_root_store_status_ok");
        rootStoreOk = LlvmApi.BuildAnd(builder,
            rootStoreOk,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, rootStoreHandle, state.I64, prefix + "_root_store_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_root_store"),
            prefix + "_root_store_ok");
        LlvmApi.BuildCondBr(builder, rootStoreOk, createPemVerifierBuilderBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createPemVerifierBuilderBlock);
        LlvmValueHandle rootStoreHandleValue = LlvmApi.BuildLoad2(builder, state.I8Ptr, rootStoreSlot, prefix + "_root_store_handle_value");
        LlvmTypeHandle pemVerifierBuilderNewType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmValueHandle pemVerifierBuilderHandle = EmitCallFunctionAddress(
            state,
            pemVerifierBuilderNewFn,
            pemVerifierBuilderNewType,
            [rootStoreHandleValue],
            prefix + "_pem_verifier_builder_new_call");
        LlvmTypeHandle rootStoreFreeType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        _ = EmitCallFunctionAddress(state, rootStoreFreeFn, rootStoreFreeType, [rootStoreHandleValue], string.Empty);
        LlvmApi.BuildStore(builder, pemVerifierBuilderHandle, pemVerifierBuilderSlot);
        LlvmValueHandle havePemVerifierBuilder = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LlvmApi.BuildPtrToInt(builder, pemVerifierBuilderHandle, state.I64, prefix + "_pem_verifier_builder_i64"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_have_pem_verifier_builder");
        LlvmApi.BuildCondBr(builder, havePemVerifierBuilder, buildPemVerifierBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildPemVerifierBlock);
        LlvmValueHandle pemVerifierBuilderHandleValue = LlvmApi.BuildLoad2(builder, state.I8Ptr, pemVerifierBuilderSlot, prefix + "_pem_verifier_builder_handle");
        LlvmTypeHandle pemVerifierBuilderBuildType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, LlvmApi.PointerTypeInContext(state.Target.Context, 0)]);
        LlvmValueHandle pemVerifierStatus = EmitCallFunctionAddress(
            state,
            pemVerifierBuilderBuildFn,
            pemVerifierBuilderBuildType,
            [pemVerifierBuilderHandleValue, verifierSlot],
            prefix + "_pem_verifier_builder_build_call");
        LlvmTypeHandle pemVerifierBuilderFreeType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        _ = EmitCallFunctionAddress(state, pemVerifierBuilderFreeFn, pemVerifierBuilderFreeType, [pemVerifierBuilderHandleValue], string.Empty);
        LlvmValueHandle pemVerifierHandle = LlvmApi.BuildLoad2(builder, state.I8Ptr, verifierSlot, prefix + "_pem_verifier_handle");
        LlvmValueHandle pemVerifierOk = LlvmApi.BuildAnd(builder,
            EmitRustlsResultIsOk(state, pemVerifierStatus, prefix + "_pem_verifier_status_ok"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, pemVerifierHandle, state.I64, prefix + "_pem_verifier_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_pem_verifier"),
            prefix + "_pem_verifier_ok");
        LlvmApi.BuildCondBr(builder, pemVerifierOk, createBuilderBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createPlatformVerifierBlock);
        LlvmTypeHandle platformVerifierType = LlvmApi.FunctionType(state.I32, [LlvmApi.PointerTypeInContext(state.Target.Context, 0)]);
        LlvmValueHandle verifierStatus = EmitCallFunctionAddress(
            state,
            platformVerifierFn,
            platformVerifierType,
            [verifierSlot],
            prefix + "_platform_verifier_call");
        LlvmValueHandle platformVerifierHandle = LlvmApi.BuildLoad2(builder, state.I8Ptr, verifierSlot, prefix + "_platform_verifier_handle");
        LlvmValueHandle verifierOk = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LlvmApi.BuildZExt(builder, verifierStatus, state.I64, prefix + "_verifier_status_i64"), LlvmApi.ConstInt(state.I64, RustlsResultOk, 0), prefix + "_verifier_status_ok"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, platformVerifierHandle, state.I64, prefix + "_verifier_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_verifier"),
            prefix + "_verifier_ok");
        LlvmApi.BuildCondBr(builder, verifierOk, createBuilderBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBuilderBlock);
        LlvmTypeHandle configBuilderNewType = LlvmApi.FunctionType(state.I8Ptr, []);
        LlvmValueHandle configBuilder = EmitCallFunctionAddress(
            state,
            configBuilderNewFn,
            configBuilderNewType,
            Array.Empty<LlvmValueHandle>(),
            prefix + "_builder_new_call");
        LlvmValueHandle haveBuilder = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, configBuilder, state.I64, prefix + "_builder_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_builder");
        LlvmApi.BuildCondBr(builder, haveBuilder, attachVerifierBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, attachVerifierBlock);
        LlvmValueHandle selectedVerifierHandle = LlvmApi.BuildLoad2(builder, state.I8Ptr, verifierSlot, prefix + "_selected_verifier_handle");
        LlvmTypeHandle setVerifierType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr, state.I8Ptr]);
        _ = EmitCallFunctionAddress(
            state,
            configBuilderSetVerifierFn,
            setVerifierType,
            [configBuilder, selectedVerifierHandle],
            string.Empty);
        LlvmTypeHandle configBuilderBuildType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, LlvmApi.PointerTypeInContext(state.Target.Context, 0)]);
        LlvmValueHandle buildStatus = EmitCallFunctionAddress(
            state,
            configBuilderBuildFn,
            configBuilderBuildType,
            [configBuilder, configSlot],
            prefix + "_build_call");
        LlvmTypeHandle verifierFreeType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        _ = EmitCallFunctionAddress(state, verifierFreeFn, verifierFreeType, [selectedVerifierHandle], string.Empty);
        LlvmValueHandle configHandle = LlvmApi.BuildLoad2(builder, state.I8Ptr, configSlot, prefix + "_config_handle");
        LlvmValueHandle buildOk = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LlvmApi.BuildZExt(builder, buildStatus, state.I64, prefix + "_build_status_i64"), LlvmApi.ConstInt(state.I64, RustlsResultOk, 0), prefix + "_build_status_ok"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, configHandle, state.I64, prefix + "_config_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_config"),
            prefix + "_build_ok");
        LlvmApi.BuildCondBr(builder, buildOk, successBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildPtrToInt(builder, configHandle, state.I64, prefix + "_ctx_store_value"), globals.ContextGlobal);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failMissingLibraryBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failMissingSymbolBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-2L)), 1), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failInitBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-3L)), 1), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_status");
    }

    private static LlvmValueHandle EmitEnsureWindowsTlsRuntimeInitialized(LlvmCodegenState state, LinuxTlsGlobals globals, HermeticTlsRuntimeAsset? rustlsSharedLibrary, LlvmValueHandle linkedTlsPayloadStartGlobal, LlvmValueHandle linkedTlsPayloadEndGlobal, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle initBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_init");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");

        LlvmValueHandle currentStatus = LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_current_status");
        LlvmValueHandle needsInit = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_needs_init");
        LlvmApi.BuildCondBr(builder, needsInit, initBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initBlock);
        if (rustlsSharedLibrary is null)
        {
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), globals.InitStatusGlobal);
            LlvmApi.BuildBr(builder, doneBlock);
            LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
            return LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_status");
        }

        LlvmBasicBlockHandle afterWriteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_after_write");
        LlvmBasicBlockHandle resolveSymbolsBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_resolve_symbols");
        LlvmBasicBlockHandle initializeConfigBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_initialize_config");
        LlvmBasicBlockHandle checkCertFileLengthBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check_cert_file_length");
        LlvmBasicBlockHandle createPemRootStoreBuilderBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_pem_root_store_builder");
        LlvmBasicBlockHandle buildPemRootStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build_pem_root_store");
        LlvmBasicBlockHandle createPemVerifierBuilderBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_pem_verifier_builder");
        LlvmBasicBlockHandle buildPemVerifierBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_build_pem_verifier");
        LlvmBasicBlockHandle createPlatformVerifierBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_platform_verifier");
        LlvmBasicBlockHandle createBuilderBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_create_builder");
        LlvmBasicBlockHandle attachVerifierBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_attach_verifier");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmBasicBlockHandle failMissingLibraryBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail_missing_library");
        LlvmBasicBlockHandle failMissingSymbolBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail_missing_symbol");
        LlvmBasicBlockHandle failInitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail_init");

        LlvmValueHandle payloadPathRef = EmitHeapStringLiteral(state, ".\\" + rustlsSharedLibrary.EmbeddedFileName);
        LlvmValueHandle payloadPathCstr = EmitStringToCString(state, payloadPathRef, prefix + "_payload_path");
        LlvmValueHandle payloadStartAddress = LlvmApi.BuildPtrToInt(builder, linkedTlsPayloadStartGlobal, state.I64, prefix + "_payload_start");
        LlvmValueHandle payloadEndAddress = LlvmApi.BuildPtrToInt(builder, linkedTlsPayloadEndGlobal, state.I64, prefix + "_payload_end");
        LlvmValueHandle payloadLength = LlvmApi.BuildSub(builder, payloadEndAddress, payloadStartAddress, prefix + "_payload_length");
        LlvmValueHandle payloadWriteResult = EmitWindowsFileWriteBytes(
            state,
            payloadPathRef,
            payloadStartAddress,
            payloadLength);
        LlvmValueHandle payloadWriteOk = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Eq,
            LoadMemory(state, payloadWriteResult, 0, prefix + "_payload_write_tag"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_payload_write_ok");
        LlvmApi.BuildCondBr(builder, payloadWriteOk, afterWriteBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, afterWriteBlock);
        LlvmValueHandle libsslHandle = EmitWindowsLoadLibrary(state, payloadPathCstr, prefix + "_load_rustls");
        LlvmApi.BuildStore(builder, libsslHandle, globals.LibsslHandleGlobal);
        LlvmValueHandle hasLibssl = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, libsslHandle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_has_rustls");
        LlvmApi.BuildCondBr(builder, hasLibssl, resolveSymbolsBlock, failMissingLibraryBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resolveSymbolsBlock);
        LlvmValueHandle platformVerifierFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_platform_server_cert_verifier", prefix + "_resolve_platform_verifier");
        LlvmValueHandle configBuilderNewFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_client_config_builder_new", prefix + "_resolve_builder_new");
        LlvmValueHandle configBuilderSetVerifierFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_client_config_builder_set_server_verifier", prefix + "_resolve_set_verifier");
        LlvmValueHandle configBuilderBuildFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_client_config_builder_build", prefix + "_resolve_build");
        LlvmValueHandle verifierFreeFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_server_cert_verifier_free", prefix + "_resolve_verifier_free");
        LlvmValueHandle rootStoreBuilderNewFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_root_cert_store_builder_new", prefix + "_resolve_root_store_builder_new");
        LlvmValueHandle rootStoreBuilderLoadRootsFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_root_cert_store_builder_load_roots_from_file", prefix + "_resolve_root_store_builder_load_roots");
        LlvmValueHandle rootStoreBuilderBuildFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_root_cert_store_builder_build", prefix + "_resolve_root_store_builder_build");
        LlvmValueHandle rootStoreBuilderFreeFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_root_cert_store_builder_free", prefix + "_resolve_root_store_builder_free");
        LlvmValueHandle rootStoreFreeFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_root_cert_store_free", prefix + "_resolve_root_store_free");
        LlvmValueHandle pemVerifierBuilderNewFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_web_pki_server_cert_verifier_builder_new", prefix + "_resolve_pem_verifier_builder_new");
        LlvmValueHandle pemVerifierBuilderBuildFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_web_pki_server_cert_verifier_builder_build", prefix + "_resolve_pem_verifier_builder_build");
        LlvmValueHandle pemVerifierBuilderFreeFn = EmitTlsResolveSymbol(state, libsslHandle, "rustls_web_pki_server_cert_verifier_builder_free", prefix + "_resolve_pem_verifier_builder_free");
        LlvmValueHandle haveAllSymbols = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, platformVerifierFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_platform_verifier"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, configBuilderNewFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_builder_new"),
            prefix + "_have_first_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, configBuilderSetVerifierFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_set_verifier"),
            prefix + "_have_second_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, configBuilderBuildFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_build"),
            prefix + "_have_third_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, verifierFreeFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_verifier_free"),
            prefix + "_have_fourth_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, rootStoreBuilderNewFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_root_store_builder_new"),
            prefix + "_have_fifth_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, rootStoreBuilderLoadRootsFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_root_store_builder_load_roots"),
            prefix + "_have_sixth_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, rootStoreBuilderBuildFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_root_store_builder_build"),
            prefix + "_have_seventh_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, rootStoreBuilderFreeFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_root_store_builder_free"),
            prefix + "_have_eighth_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, rootStoreFreeFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_root_store_free"),
            prefix + "_have_ninth_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, pemVerifierBuilderNewFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_pem_verifier_builder_new"),
            prefix + "_have_tenth_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, pemVerifierBuilderBuildFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_pem_verifier_builder_build"),
            prefix + "_have_eleventh_symbols");
        haveAllSymbols = LlvmApi.BuildAnd(builder,
            haveAllSymbols,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, pemVerifierBuilderFreeFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_pem_verifier_builder_free"),
            prefix + "_have_all_symbols");
        LlvmApi.BuildCondBr(builder, haveAllSymbols, initializeConfigBlock, failMissingSymbolBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initializeConfigBlock);
        LlvmValueHandle verifierSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, prefix + "_verifier_slot");
        LlvmValueHandle configSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, prefix + "_config_slot");
        LlvmValueHandle rootStoreBuilderSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, prefix + "_root_store_builder_slot");
        LlvmValueHandle rootStoreSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, prefix + "_root_store_slot");
        LlvmValueHandle pemVerifierBuilderSlot = LlvmApi.BuildAlloca(builder, state.I8Ptr, prefix + "_pem_verifier_builder_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), verifierSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), configSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), rootStoreBuilderSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), rootStoreSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstNull(state.I8Ptr), pemVerifierBuilderSlot);
        LlvmValueHandle certFileName = EmitStringToCString(state, EmitHeapStringLiteral(state, "SSL_CERT_FILE"), prefix + "_ssl_cert_file_name");
        LlvmValueHandle kernel32Path = EmitStringToCString(state, EmitHeapStringLiteral(state, "KERNEL32.DLL"), prefix + "_kernel32_path");
        LlvmValueHandle kernel32Handle = EmitWindowsLoadLibrary(state, kernel32Path, prefix + "_load_kernel32");
        LlvmValueHandle haveKernel32 = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, kernel32Handle, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_kernel32");
        LlvmValueHandle certFileBufferSize = LlvmApi.ConstInt(state.I32, 4096, 0);
        LlvmValueHandle certFileBufferSizeI64 = LlvmApi.ConstInt(state.I64, 4096, 0);
        LlvmValueHandle certFileBuffer = LlvmApi.BuildArrayAlloca(builder, state.I8, certFileBufferSizeI64, prefix + "_ssl_cert_file_buffer");
        LlvmBasicBlockHandle resolveGetEnvBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_resolve_get_env");
        LlvmApi.BuildCondBr(builder, haveKernel32, resolveGetEnvBlock, createPlatformVerifierBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resolveGetEnvBlock);
        LlvmValueHandle getEnvSymbol = EmitStringToCString(state, EmitHeapStringLiteral(state, "GetEnvironmentVariableA"), prefix + "_get_env_symbol");
        LlvmValueHandle getEnvFn = EmitWindowsGetProcAddress(state, kernel32Handle, getEnvSymbol, prefix + "_resolve_get_env");
        LlvmValueHandle haveGetEnv = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, getEnvFn, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_get_env");
        LlvmApi.BuildCondBr(builder, haveGetEnv, checkCertFileLengthBlock, createPlatformVerifierBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkCertFileLengthBlock);
        LlvmTypeHandle getEnvType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I32]);
        LlvmValueHandle certFileLength = EmitCallFunctionAddress(
            state,
            getEnvFn,
            getEnvType,
            [certFileName, certFileBuffer, certFileBufferSize],
            prefix + "_get_env_call");
        LlvmValueHandle hasNonEmptyCertFile = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LlvmApi.BuildZExt(builder, certFileLength, state.I64, prefix + "_ssl_cert_file_length_i64"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_has_non_empty_ssl_cert_file");
        LlvmValueHandle certFileFitsBuffer = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ult,
            certFileLength,
            certFileBufferSize,
            prefix + "_ssl_cert_file_fits_buffer");
        LlvmValueHandle usePemRoots = LlvmApi.BuildAnd(builder, hasNonEmptyCertFile, certFileFitsBuffer, prefix + "_use_pem_roots");
        LlvmApi.BuildCondBr(builder, usePemRoots, createPemRootStoreBuilderBlock, createPlatformVerifierBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createPemRootStoreBuilderBlock);
        LlvmTypeHandle rootStoreBuilderNewType = LlvmApi.FunctionType(state.I8Ptr, []);
        LlvmValueHandle rootStoreBuilderHandle = EmitCallFunctionAddress(
            state,
            rootStoreBuilderNewFn,
            rootStoreBuilderNewType,
            Array.Empty<LlvmValueHandle>(),
            prefix + "_root_store_builder_new_call");
        LlvmApi.BuildStore(builder, rootStoreBuilderHandle, rootStoreBuilderSlot);
        LlvmValueHandle haveRootStoreBuilder = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LlvmApi.BuildPtrToInt(builder, rootStoreBuilderHandle, state.I64, prefix + "_root_store_builder_i64"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_have_root_store_builder");
        LlvmApi.BuildCondBr(builder, haveRootStoreBuilder, buildPemRootStoreBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildPemRootStoreBlock);
        LlvmValueHandle rootStoreBuilderHandleValue = LlvmApi.BuildLoad2(builder, state.I8Ptr, rootStoreBuilderSlot, prefix + "_root_store_builder_handle");
        LlvmTypeHandle rootStoreBuilderLoadRootsType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I8]);
        LlvmValueHandle loadRootsStatus = EmitCallFunctionAddress(
            state,
            rootStoreBuilderLoadRootsFn,
            rootStoreBuilderLoadRootsType,
            [rootStoreBuilderHandleValue, certFileBuffer, LlvmApi.ConstInt(state.I8, 1, 0)],
            prefix + "_root_store_builder_load_roots_call");
        LlvmTypeHandle rootStoreBuilderBuildType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, LlvmApi.PointerTypeInContext(state.Target.Context, 0)]);
        LlvmValueHandle buildRootStoreStatus = EmitCallFunctionAddress(
            state,
            rootStoreBuilderBuildFn,
            rootStoreBuilderBuildType,
            [rootStoreBuilderHandleValue, rootStoreSlot],
            prefix + "_root_store_builder_build_call");
        LlvmTypeHandle rootStoreBuilderFreeType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        _ = EmitCallFunctionAddress(state, rootStoreBuilderFreeFn, rootStoreBuilderFreeType, [rootStoreBuilderHandleValue], string.Empty);
        LlvmValueHandle rootStoreHandle = LlvmApi.BuildLoad2(builder, state.I8Ptr, rootStoreSlot, prefix + "_root_store_handle");
        LlvmValueHandle rootStoreOk = LlvmApi.BuildAnd(builder,
            EmitRustlsResultIsOk(state, loadRootsStatus, prefix + "_load_roots_status_ok"),
            EmitRustlsResultIsOk(state, buildRootStoreStatus, prefix + "_build_root_store_status_ok"),
            prefix + "_root_store_status_ok");
        rootStoreOk = LlvmApi.BuildAnd(builder,
            rootStoreOk,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, rootStoreHandle, state.I64, prefix + "_root_store_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_root_store"),
            prefix + "_root_store_ok");
        LlvmApi.BuildCondBr(builder, rootStoreOk, createPemVerifierBuilderBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createPemVerifierBuilderBlock);
        LlvmValueHandle rootStoreHandleValue = LlvmApi.BuildLoad2(builder, state.I8Ptr, rootStoreSlot, prefix + "_root_store_handle_value");
        LlvmTypeHandle pemVerifierBuilderNewType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmValueHandle pemVerifierBuilderHandle = EmitCallFunctionAddress(
            state,
            pemVerifierBuilderNewFn,
            pemVerifierBuilderNewType,
            [rootStoreHandleValue],
            prefix + "_pem_verifier_builder_new_call");
        LlvmTypeHandle rootStoreFreeType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        _ = EmitCallFunctionAddress(state, rootStoreFreeFn, rootStoreFreeType, [rootStoreHandleValue], string.Empty);
        LlvmApi.BuildStore(builder, pemVerifierBuilderHandle, pemVerifierBuilderSlot);
        LlvmValueHandle havePemVerifierBuilder = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LlvmApi.BuildPtrToInt(builder, pemVerifierBuilderHandle, state.I64, prefix + "_pem_verifier_builder_i64"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            prefix + "_have_pem_verifier_builder");
        LlvmApi.BuildCondBr(builder, havePemVerifierBuilder, buildPemVerifierBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildPemVerifierBlock);
        LlvmValueHandle pemVerifierBuilderHandleValue = LlvmApi.BuildLoad2(builder, state.I8Ptr, pemVerifierBuilderSlot, prefix + "_pem_verifier_builder_handle");
        LlvmTypeHandle pemVerifierBuilderBuildType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, LlvmApi.PointerTypeInContext(state.Target.Context, 0)]);
        LlvmValueHandle pemVerifierStatus = EmitCallFunctionAddress(
            state,
            pemVerifierBuilderBuildFn,
            pemVerifierBuilderBuildType,
            [pemVerifierBuilderHandleValue, verifierSlot],
            prefix + "_pem_verifier_builder_build_call");
        LlvmTypeHandle pemVerifierBuilderFreeType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        _ = EmitCallFunctionAddress(state, pemVerifierBuilderFreeFn, pemVerifierBuilderFreeType, [pemVerifierBuilderHandleValue], string.Empty);
        LlvmValueHandle pemVerifierHandle = LlvmApi.BuildLoad2(builder, state.I8Ptr, verifierSlot, prefix + "_pem_verifier_handle");
        LlvmValueHandle pemVerifierOk = LlvmApi.BuildAnd(builder,
            EmitRustlsResultIsOk(state, pemVerifierStatus, prefix + "_pem_verifier_status_ok"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, pemVerifierHandle, state.I64, prefix + "_pem_verifier_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_pem_verifier"),
            prefix + "_pem_verifier_ok");
        LlvmApi.BuildCondBr(builder, pemVerifierOk, createBuilderBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createPlatformVerifierBlock);
        LlvmTypeHandle platformVerifierType = LlvmApi.FunctionType(state.I32, [LlvmApi.PointerTypeInContext(state.Target.Context, 0)]);
        LlvmValueHandle verifierStatus = EmitCallFunctionAddress(
            state,
            platformVerifierFn,
            platformVerifierType,
            [verifierSlot],
            prefix + "_platform_verifier_call");
        LlvmValueHandle verifierHandle = LlvmApi.BuildLoad2(builder, state.I8Ptr, verifierSlot, prefix + "_verifier_handle");
        LlvmValueHandle verifierOk = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LlvmApi.BuildZExt(builder, verifierStatus, state.I64, prefix + "_verifier_status_i64"), LlvmApi.ConstInt(state.I64, RustlsResultOk, 0), prefix + "_verifier_status_ok"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, verifierHandle, state.I64, prefix + "_verifier_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_verifier"),
            prefix + "_verifier_ok");
        LlvmApi.BuildCondBr(builder, verifierOk, createBuilderBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createBuilderBlock);
        LlvmTypeHandle configBuilderNewType = LlvmApi.FunctionType(state.I8Ptr, []);
        LlvmValueHandle configBuilder = EmitCallFunctionAddress(
            state,
            configBuilderNewFn,
            configBuilderNewType,
            Array.Empty<LlvmValueHandle>(),
            prefix + "_builder_new_call");
        LlvmValueHandle haveBuilder = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, configBuilder, state.I64, prefix + "_builder_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_builder");
        LlvmApi.BuildCondBr(builder, haveBuilder, attachVerifierBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, attachVerifierBlock);
        LlvmValueHandle selectedVerifierHandle = LlvmApi.BuildLoad2(builder, state.I8Ptr, verifierSlot, prefix + "_selected_verifier_handle");
        LlvmTypeHandle setVerifierType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr, state.I8Ptr]);
        _ = EmitCallFunctionAddress(
            state,
            configBuilderSetVerifierFn,
            setVerifierType,
            [configBuilder, selectedVerifierHandle],
            string.Empty);
        LlvmTypeHandle configBuilderBuildType = LlvmApi.FunctionType(state.I32, [state.I8Ptr, LlvmApi.PointerTypeInContext(state.Target.Context, 0)]);
        LlvmValueHandle buildStatus = EmitCallFunctionAddress(
            state,
            configBuilderBuildFn,
            configBuilderBuildType,
            [configBuilder, configSlot],
            prefix + "_build_call");
        LlvmTypeHandle verifierFreeType = LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]);
        _ = EmitCallFunctionAddress(state, verifierFreeFn, verifierFreeType, [selectedVerifierHandle], string.Empty);
        LlvmValueHandle configHandle = LlvmApi.BuildLoad2(builder, state.I8Ptr, configSlot, prefix + "_config_handle");
        LlvmValueHandle buildOk = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LlvmApi.BuildZExt(builder, buildStatus, state.I64, prefix + "_build_status_i64"), LlvmApi.ConstInt(state.I64, RustlsResultOk, 0), prefix + "_build_status_ok"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, LlvmApi.BuildPtrToInt(builder, configHandle, state.I64, prefix + "_config_i64"), LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_have_config"),
            prefix + "_build_ok");
        LlvmApi.BuildCondBr(builder, buildOk, successBlock, failInitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildPtrToInt(builder, configHandle, state.I64, prefix + "_ctx_store_value"), globals.ContextGlobal);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failMissingLibraryBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failMissingSymbolBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-2L)), 1), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failInitBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-3L)), 1), globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_status");
    }

    private static LlvmValueHandle EmitTlsInitFailureResult(LlvmCodegenState state, LlvmValueHandle initStatus)
    {
        return EmitResultError(state, EmitHeapStringLiteral(state, TlsRuntimeInitFailedMessage));
    }

    private static LlvmValueHandle EmitCallFunctionAddress(LlvmCodegenState state, LlvmValueHandle functionAddress, LlvmTypeHandle functionType, ReadOnlySpan<LlvmValueHandle> args, string name)
    {
        LlvmValueHandle functionPtr = LlvmApi.BuildIntToPtr(state.Target.Builder, functionAddress, LlvmApi.PointerTypeInContext(state.Target.Context, 0), name + "_ptr");
        return LlvmApi.BuildCall2(state.Target.Builder, functionType, functionPtr, args, name);
    }

    private static LlvmValueHandle EmitCreateTlsSession(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle sslHandle, string prefix)
    {
        LlvmValueHandle session = EmitAlloc(state, TlsSessionLayout.TotalSize);
        StoreMemory(state, session, TlsSessionLayout.Socket, socket, prefix + "_socket");
        StoreMemory(state, session, TlsSessionLayout.SslHandle, sslHandle, prefix + "_ssl");
        return session;
    }

    private static LlvmValueHandle EmitLoadTlsSessionSocket(LlvmCodegenState state, LlvmValueHandle session, string prefix)
        => LoadMemory(state, session, TlsSessionLayout.Socket, prefix + "_socket");

    private static LlvmValueHandle EmitLoadTlsSessionSsl(LlvmCodegenState state, LlvmValueHandle session, string prefix)
        => LoadMemory(state, session, TlsSessionLayout.SslHandle, prefix + "_ssl");

    private static void EmitCleanupTlsSession(LlvmCodegenState state, LinuxTlsGlobals globals, LlvmValueHandle session, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle libsslHandle = LlvmApi.BuildLoad2(builder, state.I64, globals.LibsslHandleGlobal, prefix + "_libssl_handle");
        LlvmValueHandle sslHandle = EmitLoadTlsSessionSsl(state, session, prefix + "_load_ssl");
        EmitRustlsConnectionFree(state, libsslHandle, sslHandle, prefix + "_connection_free");
        _ = EmitTcpClose(state, EmitLoadTlsSessionSocket(state, session, prefix + "_load_socket"));
    }
}
