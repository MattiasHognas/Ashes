using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private static LlvmValueHandle EmitTlsResultIsOk(LlvmCodegenState state, LlvmValueHandle result, string name)
        => LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Eq, result, LlvmApi.ConstInt(state.I32, TlsResultOk, 0), name);

    private static LlvmValueHandle EmitTlsResultIsPlaintextEmpty(LlvmCodegenState state, LlvmValueHandle result, string name)
        => LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Eq, result, LlvmApi.ConstInt(state.I32, TlsResultPlaintextEmpty, 0), name);

    private static LlvmValueHandle EmitMbedTlsResultIsOk(LlvmCodegenState state, LlvmValueHandle result, string name)
        => LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Eq, result, LlvmApi.ConstInt(state.I32, 0, 0), name);

    private static LlvmValueHandle NormalizeMbedTlsStatus(LlvmCodegenState state, LlvmValueHandle result, string name)
        => LlvmApi.BuildSelect(state.Target.Builder, EmitMbedTlsResultIsOk(state, result, name + "_ok"), LlvmApi.ConstInt(state.I32, TlsResultOk, 0), result, name);

    private static LlvmValueHandle EmitMbedTlsResultIsWantRead(LlvmCodegenState state, LlvmValueHandle result, string name)
        => LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Eq, result, LlvmApi.ConstInt(state.I32, unchecked((ulong)MbedTlsWantRead), 1), name);

    private static LlvmValueHandle EmitMbedTlsResultIsWantWrite(LlvmCodegenState state, LlvmValueHandle result, string name)
        => LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Eq, result, LlvmApi.ConstInt(state.I32, unchecked((ulong)MbedTlsWantWrite), 1), name);

    private static LlvmValueHandle EmitMbedTlsResultIsWant(LlvmCodegenState state, LlvmValueHandle result, string name)
        => LlvmApi.BuildOr(state.Target.Builder, EmitMbedTlsResultIsWantRead(state, result, name + "_read"), EmitMbedTlsResultIsWantWrite(state, result, name + "_write"), name);

    private static LlvmValueHandle EmitTlsIoResultIsWouldBlock(LlvmCodegenState state, LlvmValueHandle ioResult, string name)
        => LlvmApi.BuildICmp(
            state.Target.Builder,
            LlvmIntPredicate.Eq,
            ioResult,
            state.Flavor == LlvmCodegenFlavor.WindowsX64
                ? LlvmApi.ConstInt(state.I32, WindowsWsaErrorWouldBlock, 0)
                : LlvmApi.ConstInt(state.I32, unchecked((ulong)(-LinuxErrWouldBlock)), 0),
            name);

    private static LlvmValueHandle EmitDirectTlsSymbol(LlvmCodegenState state, string symbolName, LlvmTypeHandle functionType, string name)
    {
        LlvmValueHandle fn = LlvmApi.GetNamedFunction(state.Target.Module, symbolName);
        if (fn == default)
        {
            fn = LlvmApi.AddFunction(state.Target.Module, symbolName, functionType);
            LlvmApi.SetLinkage(fn, LlvmLinkage.External);
        }

        return LlvmApi.BuildPtrToInt(state.Target.Builder, fn, state.I64, name + "_addr");
    }

    private static LlvmValueHandle EmitMbedTlsCall(LlvmCodegenState state, string symbolName, LlvmTypeHandle functionType, ReadOnlySpan<LlvmValueHandle> args, string name)
    {
        LlvmValueHandle functionAddress = EmitDirectTlsSymbol(state, symbolName, functionType, name);
        return EmitCallFunctionAddress(state, functionAddress, functionType, args, name);
    }

    private static void EmitMbedTlsVoidCall(LlvmCodegenState state, string symbolName, LlvmTypeHandle functionType, ReadOnlySpan<LlvmValueHandle> args, string name)
    {
        LlvmValueHandle functionAddress = EmitDirectTlsSymbol(state, symbolName, functionType, name);
        _ = EmitCallFunctionAddress(state, functionAddress, functionType, args, string.Empty);
    }

    private static LlvmValueHandle EmitMbedTlsConnectionSslPtr(LlvmCodegenState state, LlvmValueHandle connectionHandle, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle connectionPtr = LlvmApi.BuildIntToPtr(builder, connectionHandle, state.I8Ptr, name + "_connection_ptr");
        return LlvmApi.BuildGEP2(builder, state.I8, connectionPtr, [LlvmApi.ConstInt(state.I64, MbedTlsConnectionSslOffset, 0)], name + "_ssl_ptr");
    }

    private static void EmitMbedTlsSetWantFlags(LlvmCodegenState state, LlvmValueHandle connectionHandle, LlvmValueHandle result, string prefix)
    {
        StoreMemory(state, connectionHandle, MbedTlsConnectionWantReadOffset, LlvmApi.BuildZExt(state.Target.Builder, EmitMbedTlsResultIsWantRead(state, result, prefix + "_want_read"), state.I64, prefix + "_want_read_i64"), prefix + "_store_want_read");
        StoreMemory(state, connectionHandle, MbedTlsConnectionWantWriteOffset, LlvmApi.BuildZExt(state.Target.Builder, EmitMbedTlsResultIsWantWrite(state, result, prefix + "_want_write"), state.I64, prefix + "_want_write_i64"), prefix + "_store_want_write");
    }

    private static void EmitMbedTlsClearWantFlags(LlvmCodegenState state, LlvmValueHandle connectionHandle, string prefix)
    {
        StoreMemory(state, connectionHandle, MbedTlsConnectionWantReadOffset, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_want_read");
        StoreMemory(state, connectionHandle, MbedTlsConnectionWantWriteOffset, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_clear_want_write");
    }

    private static void EmitMbedTlsConnectionSetSocket(
        LlvmCodegenState state,
        LinuxTlsGlobals globals,
        LlvmValueHandle connectionHandle,
        LlvmValueHandle socket,
        string name)
    {
        StoreMemory(state, connectionHandle, MbedTlsConnectionSocketOffset, socket, name + "_store_socket");
        EmitMbedTlsVoidCall(
            state,
            "mbedtls_ssl_set_bio",
            LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I8Ptr]),
            [
                EmitMbedTlsConnectionSslPtr(state, connectionHandle, name),
                LlvmApi.BuildIntToPtr(state.Target.Builder, connectionHandle, state.I8Ptr, name + "_connection_ptr"),
                globals.MbedTlsWriteCallback,
                globals.MbedTlsReadCallback,
                LlvmApi.ConstNull(state.I8Ptr)
            ],
            name);
    }

    private static LlvmValueHandle EmitTlsClientConnectionNew(
        LlvmCodegenState state,
        LlvmValueHandle configHandle,
        LlvmValueHandle serverNameCstr,
        LlvmValueHandle outConnectionSlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle connection = EmitAlloc(state, MbedTlsConnectionTotalBytes);
        LlvmValueHandle sslPtr = EmitMbedTlsConnectionSslPtr(state, connection, name);
        StoreMemory(state, connection, MbedTlsConnectionWantReadOffset, LlvmApi.ConstInt(state.I64, 0, 0), name + "_want_read_init");
        StoreMemory(state, connection, MbedTlsConnectionWantWriteOffset, LlvmApi.ConstInt(state.I64, 0, 0), name + "_want_write_init");
        StoreMemory(state, connection, MbedTlsConnectionHandshakeDoneOffset, LlvmApi.ConstInt(state.I64, 0, 0), name + "_handshake_init");
        StoreMemory(state, connection, MbedTlsConnectionSocketOffset, LlvmApi.ConstInt(state.I64, 0, 0), name + "_socket_init");
        StoreMemory(state, connection, MbedTlsConnectionVerifyFlagsOffset, LlvmApi.ConstInt(state.I64, 0, 0), name + "_verify_flags_init");

        EmitMbedTlsVoidCall(state, "mbedtls_ssl_init", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]), [sslPtr], name + "_ssl_init");
        LlvmValueHandle setupStatus = EmitMbedTlsCall(state, "mbedtls_ssl_setup", LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr]), [sslPtr, LlvmApi.BuildIntToPtr(builder, configHandle, state.I8Ptr, name + "_config_ptr")], name + "_ssl_setup");
        LlvmValueHandle hostStatus = EmitMbedTlsCall(state, "mbedtls_ssl_set_hostname", LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr]), [sslPtr, serverNameCstr], name + "_set_hostname");
        LlvmValueHandle status = LlvmApi.BuildSelect(builder, EmitMbedTlsResultIsOk(state, setupStatus, name + "_setup_ok"), hostStatus, setupStatus, name + "_status");
        LlvmBasicBlockHandle storeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_store");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_done");
        LlvmApi.BuildCondBr(builder, EmitMbedTlsResultIsOk(state, status, name + "_ok"), storeBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildIntToPtr(builder, connection, state.I8Ptr, name + "_connection_ptr"), outConnectionSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return NormalizeMbedTlsStatus(state, status, name + "_normalized");
    }

    private static LlvmValueHandle EmitTlsCertifiedKeyBuild(
        LlvmCodegenState state,
        LlvmValueHandle certPtr,
        LlvmValueHandle certLen,
        LlvmValueHandle keyPtr,
        LlvmValueHandle keyLen,
        LlvmValueHandle outKeySlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle key = EmitTlsSingletonStorage(state, "__ashes_mbedtls_certified_key", MbedTlsCertifiedKeyTotalBytes);
        LlvmValueHandle certCtx = LlvmApi.BuildIntToPtr(builder, key, state.I8Ptr, name + "_cert_ptr");
        LlvmValueHandle pkCtx = LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildAdd(builder, key, LlvmApi.ConstInt(state.I64, MbedTlsCertifiedKeyKeyOffset, 0), name + "_pk_addr"), state.I8Ptr, name + "_pk_ptr");
        EmitMbedTlsVoidCall(state, "mbedtls_x509_crt_init", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]), [certCtx], name + "_cert_init");
        EmitMbedTlsVoidCall(state, "mbedtls_pk_init", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]), [pkCtx], name + "_pk_init");
        LlvmValueHandle parseCert = EmitMbedTlsCall(state, "mbedtls_x509_crt_parse", LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I64]), [certCtx, certPtr, LlvmApi.BuildAdd(builder, certLen, LlvmApi.ConstInt(state.I64, 1, 0), name + "_cert_len_nul")], name + "_parse_cert");
        LlvmValueHandle parseKey = EmitMbedTlsCall(state, "mbedtls_pk_parse_key", LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I64, state.I8Ptr, state.I64, state.I8Ptr, state.I8Ptr]), [pkCtx, keyPtr, LlvmApi.BuildAdd(builder, keyLen, LlvmApi.ConstInt(state.I64, 1, 0), name + "_key_len_nul"), LlvmApi.ConstNull(state.I8Ptr), LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstNull(state.I8Ptr), LlvmApi.ConstNull(state.I8Ptr)], name + "_parse_key");
        LlvmValueHandle status = LlvmApi.BuildSelect(builder, EmitMbedTlsResultIsOk(state, parseCert, name + "_cert_ok"), parseKey, parseCert, name + "_status");

        LlvmBasicBlockHandle storeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_store");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_done");
        LlvmApi.BuildCondBr(builder, EmitMbedTlsResultIsOk(state, status, name + "_ok"), storeBlock, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, storeBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildIntToPtr(builder, key, state.I8Ptr, name + "_key_ptr"), outKeySlot);
        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return NormalizeMbedTlsStatus(state, status, name + "_normalized");
    }

    private static LlvmValueHandle EmitTlsServerConfigBuilderNew(LlvmCodegenState state, string name)
    {
        return EmitTlsSingletonStorage(state, "__ashes_mbedtls_server_config", MbedTlsServerConfigTotalBytes);
    }

    private static LlvmValueHandle EmitTlsServerConfigBuilderSetCertifiedKeys(
        LlvmCodegenState state,
        LlvmValueHandle builderHandle,
        LlvmValueHandle keysArraySlot,
        LlvmValueHandle count,
        string name)
    {
        _ = count;
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle keyPtr = LlvmApi.BuildPtrToInt(builder, LlvmApi.BuildLoad2(builder, state.I8Ptr, keysArraySlot, name + "_key_ptr"), state.I64, name + "_key_handle");
        StoreMemory(state, builderHandle, MbedTlsServerConfigKeyOffset, keyPtr, name + "_store_key");
        return LlvmApi.ConstInt(state.I32, TlsResultOk, 0);
    }

    private static LlvmValueHandle EmitTlsServerConfigBuilderBuild(
        LlvmCodegenState state,
        LinuxTlsGlobals globals,
        LlvmValueHandle builderHandle,
        LlvmValueHandle outConfigSlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle configPtr = LlvmApi.BuildIntToPtr(builder, builderHandle, state.I8Ptr, name + "_config_ptr");
        EmitMbedTlsVoidCall(state, "mbedtls_ssl_config_init", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]), [configPtr], name + "_config_init");
        LlvmValueHandle defaults = EmitMbedTlsCall(state, "mbedtls_ssl_config_defaults", LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I32, state.I32, state.I32]), [configPtr, LlvmApi.ConstInt(state.I32, 1, 0), LlvmApi.ConstInt(state.I32, 0, 0), LlvmApi.ConstInt(state.I32, 0, 0)], name + "_defaults");
        LlvmValueHandle keyHandle = LoadMemory(state, builderHandle, MbedTlsServerConfigKeyOffset, name + "_key");
        LlvmValueHandle certPtr = LlvmApi.BuildIntToPtr(builder, keyHandle, state.I8Ptr, name + "_cert_ptr");
        LlvmValueHandle pkPtr = LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildAdd(builder, keyHandle, LlvmApi.ConstInt(state.I64, MbedTlsCertifiedKeyKeyOffset, 0), name + "_pk_addr"), state.I8Ptr, name + "_pk_ptr");
        LlvmValueHandle runtimeHandle = LlvmApi.BuildLoad2(builder, state.I64, globals.RuntimeGlobal, name + "_runtime");
        LlvmValueHandle ctrDrbgPtr = LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildAdd(builder, runtimeHandle, LlvmApi.ConstInt(state.I64, MbedTlsRuntimeCtrDrbgOffset, 0), name + "_ctr_drbg_addr"), state.I8Ptr, name + "_ctr_drbg_ptr");
        LlvmValueHandle rngCallback = LlvmApi.BuildIntToPtr(
            builder,
            EmitDirectTlsSymbol(state, "mbedtls_ctr_drbg_random", LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I64]), name + "_rng"),
            state.I8Ptr,
            name + "_rng_ptr");
        EmitMbedTlsVoidCall(state, "mbedtls_ssl_conf_rng", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr, state.I8Ptr, state.I8Ptr]), [configPtr, rngCallback, ctrDrbgPtr], name + "_conf_rng");
        LlvmValueHandle ownCert = EmitMbedTlsCall(state, "mbedtls_ssl_conf_own_cert", LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I8Ptr]), [configPtr, certPtr, pkPtr], name + "_own_cert");
        LlvmValueHandle status = LlvmApi.BuildSelect(builder, EmitMbedTlsResultIsOk(state, defaults, name + "_defaults_ok"), ownCert, defaults, name + "_status");
        LlvmApi.BuildStore(builder, LlvmApi.BuildIntToPtr(builder, builderHandle, state.I8Ptr, name + "_out_ptr"), outConfigSlot);
        return NormalizeMbedTlsStatus(state, status, name + "_normalized");
    }

    private static LlvmValueHandle EmitTlsServerConnectionNew(
        LlvmCodegenState state,
        LlvmValueHandle configHandle,
        LlvmValueHandle outConnectionSlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle connection = EmitAlloc(state, MbedTlsConnectionTotalBytes);
        LlvmValueHandle sslPtr = EmitMbedTlsConnectionSslPtr(state, connection, name);
        StoreMemory(state, connection, MbedTlsConnectionWantReadOffset, LlvmApi.ConstInt(state.I64, 0, 0), name + "_want_read_init");
        StoreMemory(state, connection, MbedTlsConnectionWantWriteOffset, LlvmApi.ConstInt(state.I64, 0, 0), name + "_want_write_init");
        StoreMemory(state, connection, MbedTlsConnectionHandshakeDoneOffset, LlvmApi.ConstInt(state.I64, 0, 0), name + "_handshake_init");
        StoreMemory(state, connection, MbedTlsConnectionSocketOffset, LlvmApi.ConstInt(state.I64, 0, 0), name + "_socket_init");
        StoreMemory(state, connection, MbedTlsConnectionVerifyFlagsOffset, LlvmApi.ConstInt(state.I64, 0, 0), name + "_verify_flags_init");
        EmitMbedTlsVoidCall(state, "mbedtls_ssl_init", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]), [sslPtr], name + "_ssl_init");
        LlvmValueHandle setupStatus = EmitMbedTlsCall(state, "mbedtls_ssl_setup", LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr]), [sslPtr, LlvmApi.BuildIntToPtr(builder, configHandle, state.I8Ptr, name + "_config_ptr")], name + "_ssl_setup");
        LlvmBasicBlockHandle storeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_store");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_done");
        LlvmApi.BuildCondBr(builder, EmitMbedTlsResultIsOk(state, setupStatus, name + "_ok"), storeBlock, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, storeBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildIntToPtr(builder, connection, state.I8Ptr, name + "_connection_ptr"), outConnectionSlot);
        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return NormalizeMbedTlsStatus(state, setupStatus, name + "_normalized");
    }

    private static LlvmValueHandle EmitTlsConnectionWantsRead(LlvmCodegenState state, LlvmValueHandle connectionHandle, string name)
    {
        return LlvmApi.BuildTrunc(state.Target.Builder, LoadMemory(state, connectionHandle, MbedTlsConnectionWantReadOffset, name + "_want_read"), state.I8, name);
    }

    private static LlvmValueHandle EmitTlsConnectionWantsWrite(LlvmCodegenState state, LlvmValueHandle connectionHandle, string name)
    {
        return LlvmApi.BuildTrunc(state.Target.Builder, LoadMemory(state, connectionHandle, MbedTlsConnectionWantWriteOffset, name + "_want_write"), state.I8, name);
    }

    private static LlvmValueHandle EmitTlsConnectionIsHandshaking(LlvmCodegenState state, LlvmValueHandle connectionHandle, string name)
    {
        LlvmValueHandle done = LoadMemory(state, connectionHandle, MbedTlsConnectionHandshakeDoneOffset, name + "_done");
        return LlvmApi.BuildZExt(state.Target.Builder, LlvmApi.BuildICmp(state.Target.Builder, LlvmIntPredicate.Eq, done, LlvmApi.ConstInt(state.I64, 0, 0), name + "_is_handshaking"), state.I8, name);
    }

    private static LlvmValueHandle EmitTlsConnectionProcessNewPackets(LlvmCodegenState state, LlvmValueHandle connectionHandle, string name)
    {
        LlvmValueHandle rc = EmitMbedTlsCall(state, "mbedtls_ssl_handshake", LlvmApi.FunctionType(state.I32, [state.I8Ptr]), [EmitMbedTlsConnectionSslPtr(state, connectionHandle, name)], name);
        EmitMbedTlsSetWantFlags(state, connectionHandle, rc, name);
        LlvmValueHandle verifyFlags = EmitMbedTlsCall(state, "mbedtls_ssl_get_verify_result", LlvmApi.FunctionType(state.I32, [state.I8Ptr]), [EmitMbedTlsConnectionSslPtr(state, connectionHandle, name + "_verify")], name + "_verify_result");
        StoreMemory(state, connectionHandle, MbedTlsConnectionVerifyFlagsOffset, LlvmApi.BuildZExt(state.Target.Builder, verifyFlags, state.I64, name + "_verify_flags_i64"), name + "_store_verify_flags");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_success");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, name + "_done");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(state.Target.Builder, state.I32, name + "_result_slot");
        LlvmApi.BuildStore(state.Target.Builder, NormalizeMbedTlsStatus(state, rc, name + "_normalized"), resultSlot);
        LlvmApi.BuildCondBr(state.Target.Builder, EmitMbedTlsResultIsOk(state, rc, name + "_ok"), successBlock, doneBlock);
        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, successBlock);
        StoreMemory(state, connectionHandle, MbedTlsConnectionHandshakeDoneOffset, LlvmApi.ConstInt(state.I64, 1, 0), name + "_store_done");
        LlvmApi.BuildStore(state.Target.Builder, LlvmApi.ConstInt(state.I32, TlsResultOk, 0), resultSlot);
        LlvmApi.BuildBr(state.Target.Builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, doneBlock);
        LlvmValueHandle current = LlvmApi.BuildLoad2(state.Target.Builder, state.I32, resultSlot, name + "_result");
        return LlvmApi.BuildSelect(state.Target.Builder, EmitMbedTlsResultIsWant(state, rc, name + "_want"), LlvmApi.ConstInt(state.I32, TlsResultOk, 0), current, name + "_want_normalized");
    }

    private static LlvmValueHandle EmitTlsConnectionReadTls(
        LlvmCodegenState state,
        LinuxTlsGlobals globals,
        LlvmValueHandle connectionHandle,
        LlvmValueHandle socket,
        LlvmValueHandle outBytesSlot,
        string name)
    {
        _ = globals;
        _ = connectionHandle;
        _ = socket;
        LlvmApi.BuildStore(state.Target.Builder, LlvmApi.ConstInt(state.I64, 0, 0), outBytesSlot);
        return LlvmApi.ConstInt(state.I32, 0, 0);
    }

    private static LlvmValueHandle EmitTlsConnectionWriteTls(
        LlvmCodegenState state,
        LinuxTlsGlobals globals,
        LlvmValueHandle connectionHandle,
        LlvmValueHandle socket,
        LlvmValueHandle outBytesSlot,
        string name)
    {
        _ = globals;
        _ = connectionHandle;
        _ = socket;
        LlvmApi.BuildStore(state.Target.Builder, LlvmApi.ConstInt(state.I64, 0, 0), outBytesSlot);
        return LlvmApi.ConstInt(state.I32, 0, 0);
    }

    private static LlvmValueHandle EmitTlsConnectionWrite(
        LlvmCodegenState state,
        LlvmValueHandle connectionHandle,
        LlvmValueHandle bufferPtr,
        LlvmValueHandle byteCount,
        LlvmValueHandle outBytesSlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle rc = EmitMbedTlsCall(state, "mbedtls_ssl_write", LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I64]), [EmitMbedTlsConnectionSslPtr(state, connectionHandle, name), bufferPtr, byteCount], name);
        EmitMbedTlsSetWantFlags(state, connectionHandle, rc, name);
        LlvmValueHandle wroteBytes = LlvmApi.BuildSelect(
            builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, rc, LlvmApi.ConstInt(state.I32, 0, 0), name + "_positive"),
            LlvmApi.BuildSExt(builder, rc, state.I64, name + "_bytes_i64"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            name + "_bytes");
        LlvmApi.BuildStore(builder, wroteBytes, outBytesSlot);
        LlvmValueHandle success = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, rc, LlvmApi.ConstInt(state.I32, 0, 0), name + "_success");
        LlvmValueHandle okOrWant = LlvmApi.BuildOr(builder, success, EmitMbedTlsResultIsWant(state, rc, name + "_want"), name + "_ok_or_want");
        return LlvmApi.BuildSelect(builder, okOrWant, LlvmApi.ConstInt(state.I32, TlsResultOk, 0), rc, name + "_status");
    }

    private static LlvmValueHandle EmitTlsConnectionRead(
        LlvmCodegenState state,
        LlvmValueHandle connectionHandle,
        LlvmValueHandle bufferPtr,
        LlvmValueHandle byteCount,
        LlvmValueHandle outBytesSlot,
        string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle rc = EmitMbedTlsCall(state, "mbedtls_ssl_read", LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I64]), [EmitMbedTlsConnectionSslPtr(state, connectionHandle, name), bufferPtr, byteCount], name);
        EmitMbedTlsSetWantFlags(state, connectionHandle, rc, name);
        LlvmValueHandle readBytes = LlvmApi.BuildSelect(
            builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, rc, LlvmApi.ConstInt(state.I32, 0, 0), name + "_positive"),
            LlvmApi.BuildSExt(builder, rc, state.I64, name + "_bytes_i64"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            name + "_bytes");
        LlvmApi.BuildStore(builder, readBytes, outBytesSlot);
        LlvmValueHandle positive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, rc, LlvmApi.ConstInt(state.I32, 0, 0), name + "_success");
        LlvmValueHandle emptyOrWant = LlvmApi.BuildOr(builder,
            LlvmApi.BuildOr(builder, EmitMbedTlsResultIsOk(state, rc, name + "_empty"), EmitMbedTlsResultIsWant(state, rc, name + "_want"), name + "_empty_or_want"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, rc, LlvmApi.ConstInt(state.I32, unchecked((ulong)MbedTlsPeerCloseNotify), 1), name + "_peer_close"),
            name + "_empty_want_or_close");
        LlvmValueHandle normalized = LlvmApi.BuildSelect(builder, emptyOrWant, LlvmApi.ConstInt(state.I32, TlsResultPlaintextEmpty, 0), rc, name + "_empty_status");
        return LlvmApi.BuildSelect(builder, positive, LlvmApi.ConstInt(state.I32, TlsResultOk, 0), normalized, name + "_status");
    }

    private static void EmitTlsConnectionSendCloseNotify(LlvmCodegenState state, LlvmValueHandle connectionHandle, string name)
    {
        LlvmValueHandle rc = EmitMbedTlsCall(state, "mbedtls_ssl_close_notify", LlvmApi.FunctionType(state.I32, [state.I8Ptr]), [EmitMbedTlsConnectionSslPtr(state, connectionHandle, name)], name);
        EmitMbedTlsSetWantFlags(state, connectionHandle, rc, name);
    }

    private static void EmitTlsConnectionFree(LlvmCodegenState state, LlvmValueHandle connectionHandle, string name)
    {
        EmitMbedTlsVoidCall(state, "mbedtls_ssl_free", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]), [EmitMbedTlsConnectionSslPtr(state, connectionHandle, name)], name);
    }

    private static LlvmValueHandle EmitTlsErrorString(LlvmCodegenState state, LlvmValueHandle resultCode, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I8, 256);
        LlvmValueHandle buffer = LlvmApi.BuildAlloca(builder, bufferType, prefix + "_buffer");
        LlvmValueHandle bufferPtr = GetArrayElementPointer(state, bufferType, buffer, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_buffer_ptr");
        EmitMbedTlsVoidCall(state, "mbedtls_strerror", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I32, state.I8Ptr, state.I64]), [resultCode, bufferPtr, LlvmApi.ConstInt(state.I64, 256, 0)], prefix);

        return EmitHeapStringSliceFromBytesPointer(
            state,
            bufferPtr,
            EmitLinuxStrlen(state, bufferPtr, prefix + "_strlen"),
            prefix + "_message");
    }

    private static LlvmValueHandle EmitMbedTlsHandshakeErrorString(
        LlvmCodegenState state,
        LlvmValueHandle resultCode,
        LlvmValueHandle connectionHandle,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle verifyFlags = LoadMemory(state, connectionHandle, MbedTlsConnectionVerifyFlagsOffset, prefix + "_verify_flags");
        // mbedtls_ssl_get_verify_result returns (uint32_t)-1 when no verification result is
        // available (e.g. the handshake aborted on an internal error before/without completing
        // verification); every bit is set then, so it must not be read as a real flag word.
        LlvmValueHandle flagsAvailable = LlvmApi.BuildICmp(
            builder,
            LlvmIntPredicate.Ne,
            verifyFlags,
            LlvmApi.ConstInt(state.I64, 0xFFFFFFFFUL, 0),
            prefix + "_flags_available");
        LlvmValueHandle notValidForName = LlvmApi.BuildAnd(
            builder,
            flagsAvailable,
            LlvmApi.BuildICmp(
                builder,
                LlvmIntPredicate.Ne,
                LlvmApi.BuildAnd(builder, verifyFlags, LlvmApi.ConstInt(state.I64, 0x04, 0), prefix + "_name_flag"),
                LlvmApi.ConstInt(state.I64, 0, 0),
                prefix + "_name_flag_set"),
            prefix + "_not_valid_for_name");
        LlvmValueHandle unknownIssuer = LlvmApi.BuildAnd(
            builder,
            flagsAvailable,
            LlvmApi.BuildICmp(
                builder,
                LlvmIntPredicate.Ne,
                LlvmApi.BuildAnd(builder, verifyFlags, LlvmApi.ConstInt(state.I64, 0x08, 0), prefix + "_issuer_flag"),
                LlvmApi.ConstInt(state.I64, 0, 0),
                prefix + "_issuer_flag_set"),
            prefix + "_unknown_issuer");
        LlvmValueHandle hasMappedVerifyError = LlvmApi.BuildOr(builder, notValidForName, unknownIssuer, prefix + "_has_mapped_verify_error");
        LlvmValueHandle mappedMessage = LlvmApi.BuildSelect(
            builder,
            notValidForName,
            EmitHeapStringLiteral(state, "invalid peer certificate: NotValidForName"),
            EmitHeapStringLiteral(state, "invalid peer certificate: UnknownIssuer"),
            prefix + "_mapped_message");
        return LlvmApi.BuildSelect(
            builder,
            hasMappedVerifyError,
            mappedMessage,
            EmitTlsErrorString(state, resultCode, prefix + "_strerror"),
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

    private static LlvmValueHandle EmitEnsureTlsRuntimeInitialized(LlvmCodegenState state, LinuxTlsGlobals globals, bool usesTlsRuntime, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle initBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_init");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");

        LlvmValueHandle currentStatus = LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_current_status");
        LlvmValueHandle needsInit = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentStatus, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_needs_init");
        LlvmApi.BuildCondBr(builder, needsInit, initBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initBlock);
        if (!usesTlsRuntime)
        {
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), globals.InitStatusGlobal);
            LlvmApi.BuildBr(builder, doneBlock);
            LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
            return LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_status");
        }

        var setup = EmitEnsureTlsRuntimeInitializedSetup(state, prefix);
        EmitEnsureTlsRuntimeInitializedConfigure(state, globals, setup, doneBlock, prefix);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, globals.InitStatusGlobal, prefix + "_status");
    }

    private static (LlvmValueHandle Runtime, LlvmValueHandle CtrDrbgPtr, LlvmValueHandle RootsPtr, LlvmValueHandle ClientConfigPtr, LlvmValueHandle RngCallback, LlvmValueHandle SeedStatus, LlvmValueHandle DefaultsStatus) EmitEnsureTlsRuntimeInitializedSetup(
        LlvmCodegenState state, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle runtime = EmitTlsSingletonStorage(state, "__ashes_mbedtls_runtime", MbedTlsRuntimeTotalBytes);
        LlvmValueHandle entropyPtr = LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildAdd(builder, runtime, LlvmApi.ConstInt(state.I64, MbedTlsRuntimeEntropyOffset, 0), prefix + "_entropy_addr"), state.I8Ptr, prefix + "_entropy_ptr");
        LlvmValueHandle ctrDrbgPtr = LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildAdd(builder, runtime, LlvmApi.ConstInt(state.I64, MbedTlsRuntimeCtrDrbgOffset, 0), prefix + "_ctr_drbg_addr"), state.I8Ptr, prefix + "_ctr_drbg_ptr");
        LlvmValueHandle rootsPtr = LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildAdd(builder, runtime, LlvmApi.ConstInt(state.I64, MbedTlsRuntimeRootsOffset, 0), prefix + "_roots_addr"), state.I8Ptr, prefix + "_roots_ptr");
        LlvmValueHandle clientConfigPtr = LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildAdd(builder, runtime, LlvmApi.ConstInt(state.I64, MbedTlsRuntimeClientConfigOffset, 0), prefix + "_client_config_addr"), state.I8Ptr, prefix + "_client_config_ptr");

        EmitMbedTlsVoidCall(state, "mbedtls_entropy_init", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]), [entropyPtr], prefix + "_entropy_init");
        EmitMbedTlsVoidCall(state, "mbedtls_ctr_drbg_init", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]), [ctrDrbgPtr], prefix + "_ctr_drbg_init");
        EmitMbedTlsVoidCall(state, "mbedtls_x509_crt_init", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]), [rootsPtr], prefix + "_roots_init");
        EmitMbedTlsVoidCall(state, "mbedtls_ssl_config_init", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr]), [clientConfigPtr], prefix + "_client_config_init");

        LlvmValueHandle entropyCallback = LlvmApi.BuildIntToPtr(
            builder,
            EmitDirectTlsSymbol(state, "mbedtls_entropy_func", LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I64]), prefix + "_entropy_func"),
            state.I8Ptr,
            prefix + "_entropy_func_ptr");
        LlvmValueHandle rngCallback = LlvmApi.BuildIntToPtr(
            builder,
            EmitDirectTlsSymbol(state, "mbedtls_ctr_drbg_random", LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I64]), prefix + "_rng"),
            state.I8Ptr,
            prefix + "_rng_ptr");
        LlvmValueHandle seedText = EmitStringToCString(state, EmitHeapStringLiteral(state, "ashes"), prefix + "_seed_text");
        LlvmValueHandle seedStatus = EmitMbedTlsCall(
            state,
            "mbedtls_ctr_drbg_seed",
            LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I8Ptr, state.I64]),
            [ctrDrbgPtr, entropyCallback, entropyPtr, seedText, LlvmApi.ConstInt(state.I64, 5, 0)],
            prefix + "_seed");
        LlvmValueHandle defaultsStatus = EmitMbedTlsCall(
            state,
            "mbedtls_ssl_config_defaults",
            LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I32, state.I32, state.I32]),
            [clientConfigPtr, LlvmApi.ConstInt(state.I32, 0, 0), LlvmApi.ConstInt(state.I32, 0, 0), LlvmApi.ConstInt(state.I32, 0, 0)],
            prefix + "_client_defaults");
        return (runtime, ctrDrbgPtr, rootsPtr, clientConfigPtr, rngCallback, seedStatus, defaultsStatus);
    }

    private static void EmitEnsureTlsRuntimeInitializedConfigure(
        LlvmCodegenState state,
        LinuxTlsGlobals globals,
        (LlvmValueHandle Runtime, LlvmValueHandle CtrDrbgPtr, LlvmValueHandle RootsPtr, LlvmValueHandle ClientConfigPtr, LlvmValueHandle RngCallback, LlvmValueHandle SeedStatus, LlvmValueHandle DefaultsStatus) setup,
        LlvmBasicBlockHandle doneBlock,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (runtime, ctrDrbgPtr, rootsPtr, clientConfigPtr, rngCallback, seedStatus, defaultsStatus) = setup;
        LlvmValueHandle parseRootsStatusSlot = LlvmApi.BuildAlloca(builder, state.I32, prefix + "_parse_roots_status_slot");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), parseRootsStatusSlot);
        LlvmValueHandle certFile = EmitLinuxGetEnv(state, "SSL_CERT_FILE", prefix + "_ssl_cert_file");
        LlvmBasicBlockHandle parseRootsBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_roots");
        LlvmBasicBlockHandle configureBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_configure");
        LlvmApi.BuildCondBr(
            builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, certFile, LlvmApi.ConstNull(state.I8Ptr), prefix + "_has_cert_file"),
            parseRootsBlock,
            configureBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseRootsBlock);
        LlvmValueHandle parseRootsStatus = EmitMbedTlsCall(
            state,
            "mbedtls_x509_crt_parse_file",
            LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr]),
            [rootsPtr, certFile],
            prefix + "_parse_roots_file");
        LlvmApi.BuildStore(builder, parseRootsStatus, parseRootsStatusSlot);
        LlvmApi.BuildBr(builder, configureBlock);

        LlvmApi.PositionBuilderAtEnd(builder, configureBlock);
        EmitMbedTlsVoidCall(state, "mbedtls_ssl_conf_authmode", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr, state.I32]), [clientConfigPtr, LlvmApi.ConstInt(state.I32, 2, 0)], prefix + "_client_authmode");
        EmitMbedTlsVoidCall(state, "mbedtls_ssl_conf_ca_chain", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr, state.I8Ptr, state.I8Ptr]), [clientConfigPtr, rootsPtr, LlvmApi.ConstNull(state.I8Ptr)], prefix + "_client_ca_chain");
        EmitMbedTlsVoidCall(state, "mbedtls_ssl_conf_rng", LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(state.Target.Context), [state.I8Ptr, state.I8Ptr, state.I8Ptr]), [clientConfigPtr, rngCallback, ctrDrbgPtr], prefix + "_client_rng");
        LlvmValueHandle parsedRootsStatus = LlvmApi.BuildLoad2(builder, state.I32, parseRootsStatusSlot, prefix + "_parse_roots_status");
        LlvmValueHandle seedDefaultsOk = LlvmApi.BuildAnd(builder, EmitMbedTlsResultIsOk(state, seedStatus, prefix + "_seed_ok"), EmitMbedTlsResultIsOk(state, defaultsStatus, prefix + "_defaults_ok"), prefix + "_seed_defaults_ok");
        LlvmValueHandle initOk = LlvmApi.BuildAnd(builder, seedDefaultsOk, EmitMbedTlsResultIsOk(state, parsedRootsStatus, prefix + "_parse_roots_ok"), prefix + "_init_ok");
        LlvmValueHandle initStatus = LlvmApi.BuildSelect(builder, initOk, LlvmApi.ConstInt(state.I64, 1, 0), LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), prefix + "_runtime_status");
        LlvmApi.BuildStore(builder, runtime, globals.RuntimeGlobal);
        LlvmApi.BuildStore(builder, LlvmApi.BuildPtrToInt(builder, clientConfigPtr, state.I64, prefix + "_client_config_handle"), globals.ContextGlobal);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), globals.ServerConfigGlobal);
        LlvmApi.BuildStore(builder, initStatus, globals.InitStatusGlobal);
        LlvmApi.BuildBr(builder, doneBlock);
    }

    private static LlvmValueHandle EmitTlsInitFailureResult(LlvmCodegenState state, LlvmValueHandle initStatus)
    {
        _ = initStatus;
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
        LlvmValueHandle sslHandle = EmitLoadTlsSessionSsl(state, session, prefix + "_load_ssl");
        EmitTlsConnectionFree(state, sslHandle, prefix + "_connection_free");
        _ = EmitTcpClose(state, EmitLoadTlsSessionSocket(state, session, prefix + "_load_socket"));
    }
}
