using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{

    private static bool EmitDrop(LlvmCodegenState state, LlvmValueHandle value, string typeName)
    {
        switch (typeName)
        {
            case "FileHandle":
                // Auto-close the fd/HANDLE when the resource leaves scope. Fire-and-forget; a
                // double-close (after an explicit Ashes.File.close) is harmless (EBADF ignored).
                EmitFileHandleClose(state, value);
                return false;

            case "Socket":
                // Drop a socket by routing cleanup through the networking ABI.
                // The result (Result[Unit, Str]) is discarded — Drop is
                // fire-and-forget; runtime errors during cleanup are ignored.
                EmitTcpCloseAbiCall(state, value);
                return false;

            case "TlsSocket":
                EmitTlsCloseAbiCall(state, value);
                return false;

            case "Process":
                // Deterministic cleanup of an abandoned child: close the three pipe fds and reap
                // it so it can't leak fds or linger as a zombie. Fire-and-forget; double-close
                // (EBADF) and a no-child reap (ECHILD) are ignored, and the reap is non-blocking
                // (WNOHANG) so dropping a still-running child never stalls the parent.
                EmitProcessDrop(state, value);
                return false;

            case "Function":
                // A closure may carry a dropper (closure+24) that closes resources moved into it
                // when it captured-and-escaped them (deterministic close).
                EmitClosureDrop(state, value);
                return false;

            default:
                // Owned heap types (String, List, ADTs, etc.):
                // No-op per-object — bulk deallocation is handled by
                // RestoreArenaState which resets the heap cursor at scope
                // exit for copy-type scopes. The Drop instruction is kept
                // in IR for semantic correctness and resource cleanup routing.
                return false;
        }
    }

    /// <summary>
    /// Drops a closure: if it carries a resource dropper at offset 24 (non-zero), invoke it as
    /// <c>dropper(0, env)</c> to close the resources the closure owns. Ordinary closures have a zero
    /// dropper and this is a no-op.
    /// </summary>
    private static void EmitClosureDrop(LlvmCodegenState state, LlvmValueHandle closure)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle dropperCode = LoadMemory(state, closure, 24, "closure_dropper");
        LlvmValueHandle isNull = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq,
            dropperCode, LlvmApi.ConstInt(state.I64, 0, 0), "closure_dropper_is_null");

        var callBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "closure_drop_call");
        var endBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "closure_drop_end");
        LlvmApi.BuildCondBr(builder, isNull, endBlock, callBlock);

        LlvmApi.PositionBuilderAtEnd(builder, callBlock);
        LlvmValueHandle env = LoadMemory(state, closure, 8, "closure_dropper_env");
        LlvmTypeHandle dropperType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64]);
        LlvmValueHandle dropperPtr = LlvmApi.BuildIntToPtr(builder, dropperCode,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0), "closure_dropper_ptr");
        LlvmApi.BuildCall2(builder, dropperType, dropperPtr,
            [LlvmApi.ConstInt(state.I64, 0, 0), env], "closure_dropper_call");
        LlvmApi.BuildBr(builder, endBlock);

        LlvmApi.PositionBuilderAtEnd(builder, endBlock);
    }

    private static LlvmValueHandle EmitHttpRequest(LlvmCodegenState state, LlvmValueHandle urlRef, LlvmValueHandle bodyRef, bool hasBody)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_result");
        LlvmValueHandle hostSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_host");
        LlvmValueHandle pathSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_path");
        LlvmValueHandle portSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_port");
        LlvmValueHandle responseSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_response");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_socket");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_index");
        LlvmValueHandle hostStartSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_host_start");
        LlvmValueHandle hostEndSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_host_end");
        LlvmValueHandle pathStartSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_path_start");
        LlvmValueHandle pathLenSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_path_len");
        LlvmValueHandle portValueSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_port_value");
        LlvmValueHandle portDigitsSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_port_digits");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), hostSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pathSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), responseSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), socketSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 7, 0), hostStartSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), hostEndSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pathStartSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pathLenSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portDigitsSlot);

        LlvmValueHandle urlLen = LoadStringLength(state, urlRef, "http_url_len");
        LlvmValueHandle urlBytes = GetStringBytesPointer(state, urlRef, "http_url_bytes");

        var httpsCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_https_check");
        var httpCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_http_check");
        var scanHostSetupBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_scan_host_setup");
        var scanHostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_scan_host");
        var parsePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_port");
        var parsePortLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_port_loop");
        var parsePortInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_port_inspect");
        var havePathBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_have_path");
        var defaultPathBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_default_path");
        var connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_connect");
        var sendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_send");
        var recvLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_loop");
        var recvInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_inspect");
        var recvDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_done");
        var parseResponseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_response");
        var httpsErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_https_error");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_close_error");
        var malformedResponseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_malformed_response");
        var chunkedErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_chunked_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_continue");

        LlvmApi.BuildBr(builder, httpsCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpsCheckBlock);
        LlvmValueHandle httpsPrefix = EmitHeapStringLiteral(state, "https://");
        LlvmValueHandle isHttps = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitStartsWith(state, urlRef, httpsPrefix, "http_is_https"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "http_is_https_bool");
        LlvmApi.BuildCondBr(builder, isHttps, httpsErrorBlock, httpCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpCheckBlock);
        LlvmValueHandle httpPrefix = EmitHeapStringLiteral(state, "http://");
        LlvmValueHandle isHttp = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitStartsWith(state, urlRef, httpPrefix, "http_is_http"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "http_is_http_bool");
        var malformedUrlBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_malformed_url");
        LlvmApi.BuildCondBr(builder, isHttp, scanHostSetupBlock, malformedUrlBlock);

        LlvmApi.PositionBuilderAtEnd(builder, malformedUrlBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedUrlMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanHostSetupBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 7, 0), indexSlot);
        LlvmApi.BuildBr(builder, scanHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanHostBlock);
        LlvmValueHandle hostLoopIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_host_loop_index");
        LlvmValueHandle hostLoopDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, hostLoopIndex, urlLen, "http_host_loop_done");
        var hostInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_inspect");
        LlvmApi.BuildCondBr(builder, hostLoopDone, defaultPathBlock, hostInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostInspectBlock);
        LlvmValueHandle hostByte = LoadByteAt(state, urlBytes, hostLoopIndex, "http_host_byte");
        LlvmValueHandle isColon = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostByte, LlvmApi.ConstInt(state.I8, (byte)':', 0), "http_host_is_colon");
        var hostCheckSlashBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_check_slash");
        LlvmApi.BuildCondBr(builder, isColon, parsePortBlock, hostCheckSlashBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostCheckSlashBlock);
        LlvmValueHandle isSlash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostByte, LlvmApi.ConstInt(state.I8, (byte)'/', 0), "http_host_is_slash");
        var hostRejectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_reject");
        var hostAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_advance");
        LlvmApi.BuildCondBr(builder, isSlash, defaultPathBlock, hostRejectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostRejectBlock);
        LlvmValueHandle isQuestion = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostByte, LlvmApi.ConstInt(state.I8, (byte)'?', 0), "http_host_is_question");
        var hostHashCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_hash_check");
        LlvmApi.BuildCondBr(builder, isQuestion, malformedUrlBlock, hostHashCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostHashCheckBlock);
        LlvmValueHandle isHash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostByte, LlvmApi.ConstInt(state.I8, (byte)'#', 0), "http_host_is_hash");
        LlvmApi.BuildCondBr(builder, isHash, malformedUrlBlock, hostAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostAdvanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, hostLoopIndex, LlvmApi.ConstInt(state.I64, 1, 0), "http_host_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, scanHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortBlock);
        LlvmValueHandle hostEnd = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_host_end");
        LlvmValueHandle hostLenValue = LlvmApi.BuildSub(builder, hostEnd, LlvmApi.ConstInt(state.I64, 7, 0), "http_host_len_before_port");
        LlvmValueHandle missingHost = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostLenValue, LlvmApi.ConstInt(state.I64, 0, 0), "http_missing_host");
        var parsePortSetupBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_port_setup");
        LlvmApi.BuildCondBr(builder, missingHost, malformedUrlBlock, parsePortSetupBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortSetupBlock);
        LlvmApi.BuildStore(builder, hostEnd, hostEndSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portDigitsSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, hostEnd, LlvmApi.ConstInt(state.I64, 1, 0), "http_port_index_start"), indexSlot);
        LlvmApi.BuildBr(builder, parsePortLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortLoopBlock);
        LlvmValueHandle portIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_port_index");
        LlvmValueHandle portDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, portIndex, urlLen, "http_port_done");
        LlvmApi.BuildCondBr(builder, portDone, defaultPathBlock, parsePortInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortInspectBlock);
        LlvmValueHandle portByte = LoadByteAt(state, urlBytes, portIndex, "http_port_byte");
        LlvmValueHandle portIsSlash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, portByte, LlvmApi.ConstInt(state.I8, (byte)'/', 0), "http_port_is_slash");
        var portDigitCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_port_digit_check");
        LlvmApi.BuildCondBr(builder, portIsSlash, defaultPathBlock, portDigitCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, portDigitCheckBlock);
        LlvmValueHandle portDigitValue = LlvmApi.BuildZExt(builder, portByte, state.I64, "http_port_digit_value");
        LlvmValueHandle portIsDigit = BuildByteRangeCheck(state, portDigitValue, (byte)'0', (byte)'9', "http_port_digit_range");
        var portAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_port_advance");
        LlvmApi.BuildCondBr(builder, portIsDigit, portAdvanceBlock, malformedUrlBlock);

        LlvmApi.PositionBuilderAtEnd(builder, portAdvanceBlock);
        LlvmValueHandle currentPort = LlvmApi.BuildLoad2(builder, state.I64, portValueSlot, "http_port_current");
        LlvmValueHandle parsedDigit = LlvmApi.BuildSub(builder, portDigitValue, LlvmApi.ConstInt(state.I64, (byte)'0', 0), "http_parsed_digit");
        LlvmValueHandle nextPort = LlvmApi.BuildAdd(builder, LlvmApi.BuildMul(builder, currentPort, LlvmApi.ConstInt(state.I64, 10, 0), "http_port_mul"), parsedDigit, "http_port_next");
        LlvmValueHandle tooLargePort = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, nextPort, LlvmApi.ConstInt(state.I64, 65535, 0), "http_port_too_large");
        var storePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_store_port");
        LlvmApi.BuildCondBr(builder, tooLargePort, malformedUrlBlock, storePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storePortBlock);
        LlvmApi.BuildStore(builder, nextPort, portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I64, portDigitsSlot, "http_port_digits_value"), LlvmApi.ConstInt(state.I64, 1, 0), "http_port_digits_next"), portDigitsSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, portIndex, LlvmApi.ConstInt(state.I64, 1, 0), "http_port_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, parsePortLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, defaultPathBlock);
        LlvmValueHandle finalHostEnd = LlvmApi.BuildLoad2(builder, state.I64, hostEndSlot, "http_final_host_end");
        LlvmValueHandle hostEndUnset = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, finalHostEnd, LlvmApi.ConstInt(state.I64, 0, 0), "http_host_end_unset");
        var setHostEndBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_set_host_end");
        var buildHostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_build_host");
        LlvmApi.BuildCondBr(builder, hostEndUnset, setHostEndBlock, buildHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, setHostEndBlock);
        LlvmValueHandle currentIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_current_index");
        LlvmValueHandle hostLenAtEnd = LlvmApi.BuildSub(builder, currentIndex, LlvmApi.ConstInt(state.I64, 7, 0), "http_host_len_at_end");
        LlvmValueHandle noHost = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostLenAtEnd, LlvmApi.ConstInt(state.I64, 0, 0), "http_no_host");
        LlvmApi.BuildCondBr(builder, noHost, malformedUrlBlock, buildHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildHostBlock);
        LlvmValueHandle actualHostEnd = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LlvmApi.BuildLoad2(builder, state.I64, hostEndSlot, "http_host_end_existing"), LlvmApi.ConstInt(state.I64, 0, 0), "http_host_end_is_zero"),
            LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_host_end_from_index"),
            LlvmApi.BuildLoad2(builder, state.I64, hostEndSlot, "http_host_end_final"),
            "http_actual_host_end");
        LlvmValueHandle actualHostLen = LlvmApi.BuildSub(builder, actualHostEnd, LlvmApi.ConstInt(state.I64, 7, 0), "http_actual_host_len");
        LlvmValueHandle hostPtr = LlvmApi.BuildGEP2(builder, state.I8, urlBytes, [LlvmApi.ConstInt(state.I64, 7, 0)], "http_host_ptr");
        LlvmApi.BuildStore(builder, EmitHeapStringSliceFromBytesPointer(state, hostPtr, actualHostLen, "http_host"), hostSlot);
        LlvmValueHandle digitsCount = LlvmApi.BuildLoad2(builder, state.I64, portDigitsSlot, "http_digits_count");
        LlvmValueHandle hasPortDigits = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, digitsCount, LlvmApi.ConstInt(state.I64, 0, 0), "http_has_port_digits");
        var storeParsedPortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_store_parsed_port");
        LlvmApi.BuildCondBr(builder, hasPortDigits, storeParsedPortBlock, havePathBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeParsedPortBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, portValueSlot, "http_port_value_final"), portSlot);
        LlvmApi.BuildBr(builder, havePathBlock);

        LlvmApi.PositionBuilderAtEnd(builder, havePathBlock);
        LlvmValueHandle pathIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_path_index");
        LlvmValueHandle hasExplicitPath = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, pathIndex, urlLen, "http_has_explicit_path");
        var explicitPathBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_explicit_path");
        var defaultPathStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_default_path_store");
        LlvmApi.BuildCondBr(builder, hasExplicitPath, explicitPathBlock, defaultPathStoreBlock);

        LlvmApi.PositionBuilderAtEnd(builder, explicitPathBlock);
        LlvmValueHandle explicitPathPtr = LlvmApi.BuildGEP2(builder, state.I8, urlBytes, [pathIndex], "http_explicit_path_ptr");
        LlvmValueHandle explicitPathLen = LlvmApi.BuildSub(builder, urlLen, pathIndex, "http_explicit_path_len");
        LlvmApi.BuildStore(builder, EmitHeapStringSliceFromBytesPointer(state, explicitPathPtr, explicitPathLen, "http_path"), pathSlot);
        LlvmApi.BuildBr(builder, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, defaultPathStoreBlock);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, "/"), pathSlot);
        LlvmApi.BuildBr(builder, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectBlock);
        LlvmValueHandle connectResult = EmitTcpConnect(state, LlvmApi.BuildLoad2(builder, state.I64, hostSlot, "http_host_value"), LlvmApi.BuildLoad2(builder, state.I64, portSlot, "http_port_value"));
        LlvmValueHandle connectTag = LoadMemory(state, connectResult, 0, "http_connect_tag");
        LlvmValueHandle connectFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, connectTag, LlvmApi.ConstInt(state.I64, 0, 0), "http_connect_failed");
        var connectStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_connect_store");
        LlvmApi.BuildCondBr(builder, connectFailed, connectStoreBlock, sendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectStoreBlock);
        LlvmApi.BuildStore(builder, connectResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendBlock);
        LlvmValueHandle socketValue = LoadMemory(state, connectResult, 8, "http_socket_value");
        LlvmApi.BuildStore(builder, socketValue, socketSlot);
        LlvmValueHandle requestRef = EmitHttpRequestString(state, LlvmApi.BuildLoad2(builder, state.I64, pathSlot, "http_path_value"), LlvmApi.BuildLoad2(builder, state.I64, hostSlot, "http_host_header_value"), bodyRef, hasBody);
        LlvmValueHandle sendResult = EmitTcpSend(state, socketValue, requestRef);
        LlvmValueHandle sendTag = LoadMemory(state, sendResult, 0, "http_send_tag");
        LlvmValueHandle sendFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sendTag, LlvmApi.ConstInt(state.I64, 0, 0), "http_send_failed");
        var sendErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_send_error");
        LlvmApi.BuildCondBr(builder, sendFailed, sendErrorBlock, recvLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendErrorBlock);
        EmitTcpClose(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "http_send_error_socket"));
        LlvmApi.BuildStore(builder, sendResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvLoopBlock);
        LlvmValueHandle recvResult = EmitTcpReceive(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "http_recv_socket"), LlvmApi.ConstInt(state.I64, 65536, 0));
        LlvmValueHandle recvTag = LoadMemory(state, recvResult, 0, "http_recv_tag");
        LlvmValueHandle recvFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, recvTag, LlvmApi.ConstInt(state.I64, 0, 0), "http_recv_failed");
        var recvErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_error");
        LlvmApi.BuildCondBr(builder, recvFailed, recvErrorBlock, recvInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvErrorBlock);
        EmitTcpClose(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "http_recv_error_socket"));
        LlvmApi.BuildStore(builder, recvResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvInspectBlock);
        LlvmValueHandle chunkRef = LoadMemory(state, recvResult, 8, "http_chunk_ref");
        LlvmValueHandle chunkLen = LoadStringLength(state, chunkRef, "http_chunk_len");
        LlvmValueHandle chunkEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, chunkLen, LlvmApi.ConstInt(state.I64, 0, 0), "http_chunk_empty");
        var recvAppendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_append");
        LlvmApi.BuildCondBr(builder, chunkEmpty, recvDoneBlock, recvAppendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvAppendBlock);
        LlvmValueHandle currentResponse = LlvmApi.BuildLoad2(builder, state.I64, responseSlot, "http_current_response");
        LlvmValueHandle hasResponse = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, currentResponse, LlvmApi.ConstInt(state.I64, 0, 0), "http_has_response");
        var concatResponseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_concat_response");
        var storeFirstChunkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_store_first_chunk");
        LlvmApi.BuildCondBr(builder, hasResponse, concatResponseBlock, storeFirstChunkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeFirstChunkBlock);
        LlvmApi.BuildStore(builder, chunkRef, responseSlot);
        LlvmApi.BuildBr(builder, recvLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, concatResponseBlock);
        LlvmApi.BuildStore(builder, EmitStringConcat(state, currentResponse, chunkRef), responseSlot);
        LlvmApi.BuildBr(builder, recvLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvDoneBlock);
        LlvmValueHandle closeResult = EmitTcpClose(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "http_close_socket"));
        LlvmValueHandle closeTag = LoadMemory(state, closeResult, 0, "http_close_tag");
        LlvmValueHandle closeFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, closeTag, LlvmApi.ConstInt(state.I64, 0, 0), "http_close_failed");
        LlvmApi.BuildCondBr(builder, closeFailed, closeErrorBlock, parseResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseResponseBlock);
        LlvmValueHandle responseRef = LlvmApi.BuildLoad2(builder, state.I64, responseSlot, "http_response_value");
        LlvmValueHandle emptyResponse = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, responseRef, LlvmApi.ConstInt(state.I64, 0, 0), "http_empty_response");
        var ensureEmptyResponseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_ensure_empty_response");
        var parseResponseContinueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_response_continue");
        LlvmApi.BuildCondBr(builder, emptyResponse, ensureEmptyResponseBlock, parseResponseContinueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, ensureEmptyResponseBlock);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, string.Empty), responseSlot);
        LlvmApi.BuildBr(builder, parseResponseContinueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseResponseContinueBlock);
        LlvmValueHandle finalResponse = LlvmApi.BuildLoad2(builder, state.I64, responseSlot, "http_final_response");
        LlvmValueHandle responseLen = LoadStringLength(state, finalResponse, "http_response_len");
        LlvmValueHandle responseTooShort = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, responseLen, LlvmApi.ConstInt(state.I64, 12, 0), "http_response_too_short");
        var parseHeadersBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_headers");
        LlvmApi.BuildCondBr(builder, responseTooShort, malformedResponseBlock, parseHeadersBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseHeadersBlock);
        LlvmValueHandle responseBytes = GetStringBytesPointer(state, finalResponse, "http_response_bytes");
        LlvmValueHandle separatorIndex = EmitFindByteSequence(state, responseBytes, responseLen, "\r\n\r\n"u8.ToArray(), "http_separator");
        LlvmValueHandle hasSeparator = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, separatorIndex, LlvmApi.ConstInt(state.I64, 0, 1), "http_has_separator");
        var parseStatusBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_status");
        LlvmApi.BuildCondBr(builder, hasSeparator, parseStatusBlock, malformedResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseStatusBlock);
        LlvmValueHandle headerLength = separatorIndex;
        LlvmValueHandle statusSpaceIndex = EmitFindByte(state, responseBytes, headerLength, 0, (byte)' ', "http_status_space");
        LlvmValueHandle hasStatusSpace = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 0, 1), "http_has_status_space");
        var parseDigitsBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_digits");
        LlvmApi.BuildCondBr(builder, hasStatusSpace, parseDigitsBlock, malformedResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseDigitsBlock);
        LlvmValueHandle statusEnd = LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 3, 0), "http_status_end");
        LlvmValueHandle digitsInRange = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, statusEnd, headerLength, "http_status_digits_in_range");
        var parseDigitsContinueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_digits_continue");
        LlvmApi.BuildCondBr(builder, digitsInRange, parseDigitsContinueBlock, malformedResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseDigitsContinueBlock);
        LlvmValueHandle hundredsByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 1, 0), "http_hundreds_idx"), "http_hundreds_byte");
        LlvmValueHandle tensByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 2, 0), "http_tens_idx"), "http_tens_byte");
        LlvmValueHandle onesByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 3, 0), "http_ones_idx"), "http_ones_byte");
        LlvmValueHandle digitsValid = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildAnd(builder,
                BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, hundredsByte, state.I64, "http_hundreds_i64"), (byte)'0', (byte)'9', "http_hundreds_range"),
                BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, tensByte, state.I64, "http_tens_i64"), (byte)'0', (byte)'9', "http_tens_range"),
                "http_digits_first"),
            BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, onesByte, state.I64, "http_ones_i64"), (byte)'0', (byte)'9', "http_ones_range"),
            "http_digits_valid");
        var detectChunkedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_detect_chunked");
        LlvmApi.BuildCondBr(builder, digitsValid, detectChunkedBlock, malformedResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, detectChunkedBlock);
        LlvmValueHandle chunkedHeaderIndex = EmitFindByteSequence(state, responseBytes, headerLength, "Transfer-Encoding: chunked"u8.ToArray(), "http_chunked_header");
        LlvmValueHandle hasChunkedHeader = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, chunkedHeaderIndex, LlvmApi.ConstInt(state.I64, 0, 1), "http_has_chunked_header");
        var buildBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_build_body");
        LlvmApi.BuildCondBr(builder, hasChunkedHeader, chunkedErrorBlock, buildBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildBodyBlock);
        LlvmValueHandle statusCode = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildAdd(builder,
                LlvmApi.BuildMul(builder, LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, hundredsByte, state.I64, "http_hundreds_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), "http_hundreds_digit"), LlvmApi.ConstInt(state.I64, 100, 0), "http_hundreds_mul"),
                LlvmApi.BuildMul(builder, LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, tensByte, state.I64, "http_tens_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), "http_tens_digit"), LlvmApi.ConstInt(state.I64, 10, 0), "http_tens_mul"),
                "http_status_prefix_sum"),
            LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, onesByte, state.I64, "http_ones_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), "http_ones_digit"),
            "http_status_code");
        LlvmValueHandle bodyStart = LlvmApi.BuildAdd(builder, separatorIndex, LlvmApi.ConstInt(state.I64, 4, 0), "http_body_start");
        LlvmValueHandle bodyLength = LlvmApi.BuildSub(builder, responseLen, bodyStart, "http_body_len");
        LlvmValueHandle bodyBytes = LlvmApi.BuildGEP2(builder, state.I8, responseBytes, [bodyStart], "http_body_ptr");
        LlvmValueHandle bodyString = EmitHeapStringSliceFromBytesPointer(state, bodyBytes, bodyLength, "http_body");
        LlvmValueHandle statusOk = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, statusCode, LlvmApi.ConstInt(state.I64, 200, 0), "http_status_ge_200"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, statusCode, LlvmApi.ConstInt(state.I64, 299, 0), "http_status_le_299"),
            "http_status_ok");
        var statusOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_status_ok_block");
        var statusErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_status_error_block");
        LlvmApi.BuildCondBr(builder, statusOk, statusOkBlock, statusErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, statusOkBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, bodyString), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, statusErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHttpStatusErrorString(state, statusCode, "http_status_error")), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpsErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpHttpsNotSupportedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        LlvmApi.BuildStore(builder, closeResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, malformedResponseBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedResponseMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, chunkedErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpUnsupportedTransferEncodingMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "http_result_value");
    }

    private static LlvmValueHandle EmitHttpRequestString(LlvmCodegenState state, LlvmValueHandle pathRef, LlvmValueHandle hostRef, LlvmValueHandle bodyRef, bool hasBody)
    {
        LlvmValueHandle request = EmitHeapStringLiteral(state, hasBody ? "POST " : "GET ");
        request = EmitStringConcat(state, request, pathRef);
        request = EmitStringConcat(state, request, EmitHeapStringLiteral(state, " HTTP/1.1\r\nHost: "));
        request = EmitStringConcat(state, request, hostRef);
        if (hasBody)
        {
            request = EmitStringConcat(state, request, EmitHeapStringLiteral(state, "\r\nContent-Length: "));
            request = EmitStringConcat(state, request, EmitNonNegativeIntToString(state, LoadStringLength(state, bodyRef, "http_body_length"), "http_body_length_string"));
        }

        request = EmitStringConcat(state, request, EmitHeapStringLiteral(state, "\r\nConnection: close\r\n\r\n"));
        if (hasBody)
        {
            request = EmitStringConcat(state, request, bodyRef);
        }

        return request;
    }

    private static LlvmValueHandle EmitHttpStatusErrorString(LlvmCodegenState state, LlvmValueHandle statusCode, string prefix)
    {
        return EmitStringConcat(state, EmitHeapStringLiteral(state, "HTTP "), EmitNonNegativeIntToString(state, statusCode, prefix + "_code"));
    }
}
