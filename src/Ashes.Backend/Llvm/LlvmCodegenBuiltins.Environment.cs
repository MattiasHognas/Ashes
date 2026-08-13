using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private const int EnvironmentPathBufferSize = 4096;
    private const string EnvironmentReadFailedMessage = "Failed to read process environment.";
    private const string EnvironmentInvalidUtf8Message = "Process environment contains invalid UTF-8.";

    private static LlvmValueHandle EmitEnvironmentDirectory(
        LlvmCodegenState state,
        EnvironmentDirectoryKind kind)
    {
        return kind switch
        {
            EnvironmentDirectoryKind.Current => EmitEnvironmentPlatformDirectory(state, kind),
            EnvironmentDirectoryKind.Executable => EmitEnvironmentPlatformDirectory(state, kind),
            EnvironmentDirectoryKind.Temporary when IsWindowsFlavor(state.Flavor) =>
                EmitEnvironmentPlatformDirectory(state, kind),
            EnvironmentDirectoryKind.Temporary => EmitEnvironmentFallbackDirectory(state, "TMPDIR", "/tmp", "env_temp"),
            EnvironmentDirectoryKind.Cache when IsWindowsFlavor(state.Flavor) =>
                EmitEnvironmentFallbackDirectory(state, "LOCALAPPDATA", "USERPROFILE", "env_cache", "\\AppData\\Local"),
            EnvironmentDirectoryKind.Cache =>
                EmitEnvironmentFallbackDirectory(state, "XDG_CACHE_HOME", "HOME", "env_cache", "/.cache"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static bool EmitEnvironmentInstruction(LlvmCodegenState state, IrInst instruction) => instruction switch
    {
        IrInst.EnvironmentDirectory directory => StoreTemp(state, directory.Target, EmitEnvironmentDirectory(state, directory.Kind)),
        IrInst.EnvironmentGet get => StoreTemp(state, get.Target, EmitEnvironmentGet(state, LoadTemp(state, get.NameTemp))),
        _ => throw new ArgumentOutOfRangeException(nameof(instruction))
    };

    private static LlvmValueHandle EmitEnvironmentPlatformDirectory(
        LlvmCodegenState state,
        EnvironmentDirectoryKind kind)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I8, EnvironmentPathBufferSize);
        LlvmValueHandle buffer = LlvmApi.BuildAlloca(builder, bufferType, "env_dir_buffer");
        LlvmValueHandle bufferPtr = GetArrayElementPointer(
            state,
            bufferType,
            buffer,
            LlvmApi.ConstInt(state.I64, 0, 0),
            "env_dir_buffer_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, 0, 0), bufferPtr);
        LlvmValueHandle length;
        LlvmValueHandle failed;

        if (IsWindowsFlavor(state.Flavor))
        {
            (length, failed) = EmitWindowsEnvironmentPlatformDirectory(state, kind, bufferPtr);
        }
        else if (kind == EnvironmentDirectoryKind.Current)
        {
            LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr, state.I64]);
            LlvmValueHandle function = EmitOrDeclareExternalFunction(state, "getcwd", functionType);
            LlvmValueHandle returned = LlvmApi.BuildCall2(builder, functionType, function,
                [bufferPtr, LlvmApi.ConstInt(state.I64, EnvironmentPathBufferSize, 0)], "env_getcwd_call");
            failed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, returned, LlvmApi.ConstNull(state.I8Ptr), "env_getcwd_failed");
            length = EmitLinuxStrlen(state, bufferPtr, "env_getcwd_strlen");
        }
        else
        {
            LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I64, [state.I8Ptr, state.I8Ptr, state.I64]);
            LlvmValueHandle function = EmitOrDeclareExternalFunction(state, "readlink", functionType);
            LlvmValueHandle path = EmitStringToCString(state, EmitHeapStringLiteral(state, "/proc/self/exe"), "env_exe_path");
            length = LlvmApi.BuildCall2(builder, functionType, function,
                [path, bufferPtr, LlvmApi.ConstInt(state.I64, EnvironmentPathBufferSize, 0)], "env_readlink_call");
            failed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, length, LlvmApi.ConstInt(state.I64, 0, 0), "env_readlink_failed");
        }

        length = LlvmApi.BuildSelect(
            builder,
            failed,
            LlvmApi.ConstInt(state.I64, 0, 0),
            length,
            "env_dir_safe_length");
        if (kind == EnvironmentDirectoryKind.Executable)
        {
            length = EmitParentPathLength(state, bufferPtr, length, "env_exe_parent");
        }

        return EmitEnvironmentUtf8Result(state, bufferPtr, length, failed, "env_dir");
    }

    private static (LlvmValueHandle Length, LlvmValueHandle Failed) EmitWindowsEnvironmentPlatformDirectory(
        LlvmCodegenState state,
        EnvironmentDirectoryKind kind,
        LlvmValueHandle bufferPtr)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        string symbol = kind switch
        {
            EnvironmentDirectoryKind.Current => "GetCurrentDirectoryA",
            EnvironmentDirectoryKind.Executable => "GetModuleFileNameA",
            EnvironmentDirectoryKind.Temporary => "GetTempPathA",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        LlvmTypeHandle functionType = kind == EnvironmentDirectoryKind.Executable
            ? LlvmApi.FunctionType(state.I32, [state.I8Ptr, state.I8Ptr, state.I32])
            : LlvmApi.FunctionType(state.I32, [state.I32, state.I8Ptr]);
        LlvmValueHandle function = EmitOrDeclareExternalFunction(state, symbol, functionType);
        LlvmValueHandle count = kind == EnvironmentDirectoryKind.Executable
            ? LlvmApi.BuildCall2(builder, functionType, function,
                [LlvmApi.ConstNull(state.I8Ptr), bufferPtr, LlvmApi.ConstInt(state.I32, EnvironmentPathBufferSize, 0)],
                "env_dir_call")
            : LlvmApi.BuildCall2(builder, functionType, function,
                [LlvmApi.ConstInt(state.I32, EnvironmentPathBufferSize, 0), bufferPtr],
                "env_dir_call");
        LlvmValueHandle length = LlvmApi.BuildZExt(builder, count, state.I64, "env_dir_length");
        LlvmValueHandle zero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, count, LlvmApi.ConstInt(state.I32, 0, 0), "env_dir_zero");
        LlvmValueHandle tooLong = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, count, LlvmApi.ConstInt(state.I32, EnvironmentPathBufferSize, 0), "env_dir_too_long");
        return (length, LlvmApi.BuildOr(builder, zero, tooLong, "env_dir_failed"));
    }

    private static LlvmValueHandle EmitEnvironmentFallbackDirectory(
        LlvmCodegenState state,
        string preferredName,
        string fallbackNameOrValue,
        string prefix,
        string fallbackSuffix = "")
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmValueHandle preferred = EmitEnvironmentRawGet(state, preferredName, prefix + "_preferred");
        LlvmBasicBlockHandle preferredBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_preferred_value");
        LlvmBasicBlockHandle fallbackBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fallback");
        LlvmBasicBlockHandle missingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_missing");
        LlvmBasicBlockHandle invalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid_utf8");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmValueHandle preferredMissing = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, preferred, LlvmApi.ConstNull(state.I8Ptr), prefix + "_preferred_missing");
        LlvmApi.BuildCondBr(builder, preferredMissing, fallbackBlock, preferredBlock);

        LlvmApi.PositionBuilderAtEnd(builder, preferredBlock);
        LlvmValueHandle preferredLength = EmitLinuxStrlen(state, preferred, prefix + "_preferred_length");
        LlvmValueHandle preferredEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, preferredLength, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_preferred_empty");
        LlvmBasicBlockHandle preferredOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_preferred_ok");
        LlvmApi.BuildCondBr(builder, preferredEmpty, fallbackBlock, preferredOkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, preferredOkBlock);
        EmitEnvironmentFallbackValue(state, preferred, preferredLength, "", resultSlot, invalidBlock, doneBlock, prefix + "_preferred");

        LlvmApi.PositionBuilderAtEnd(builder, fallbackBlock);
        if (fallbackSuffix.Length == 0)
        {
            LlvmApi.BuildStore(builder, EmitResultOk(state, EmitHeapStringLiteral(state, fallbackNameOrValue)), resultSlot);
            LlvmApi.BuildBr(builder, doneBlock);
        }
        else
        {
            LlvmValueHandle fallback = EmitEnvironmentRawGet(state, fallbackNameOrValue, prefix + "_fallback_env");
            LlvmValueHandle fallbackMissing = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, fallback, LlvmApi.ConstNull(state.I8Ptr), prefix + "_fallback_missing");
            LlvmBasicBlockHandle fallbackValueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fallback_value");
            LlvmApi.BuildCondBr(builder, fallbackMissing, missingBlock, fallbackValueBlock);
            LlvmApi.PositionBuilderAtEnd(builder, fallbackValueBlock);
            LlvmValueHandle fallbackLength = EmitLinuxStrlen(state, fallback, prefix + "_fallback_length");
            EmitEnvironmentFallbackValue(state, fallback, fallbackLength, fallbackSuffix, resultSlot, invalidBlock, doneBlock, prefix + "_fallback");
        }

        LlvmApi.PositionBuilderAtEnd(builder, missingBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, EnvironmentReadFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, EnvironmentInvalidUtf8Message)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static void EmitEnvironmentFallbackValue(
        LlvmCodegenState state,
        LlvmValueHandle bytes,
        LlvmValueHandle length,
        string suffix,
        LlvmValueHandle resultSlot,
        LlvmBasicBlockHandle invalidBlock,
        LlvmBasicBlockHandle doneBlock,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle validBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_valid");
        LlvmValueHandle utf8 = EmitValidateUtf8(state, bytes, length, prefix + "_utf8");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_valid"),
            validBlock, invalidBlock);
        LlvmApi.PositionBuilderAtEnd(builder, validBlock);
        LlvmValueHandle value = EmitHeapStringSliceFromBytesPointer(state, bytes, length, prefix + "_string");
        if (suffix.Length != 0)
        {
            value = EmitStringConcat(state, value, EmitHeapStringLiteral(state, suffix));
        }

        LlvmApi.BuildStore(builder, EmitResultOk(state, value), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
    }

    private static LlvmValueHandle EmitEnvironmentGet(LlvmCodegenState state, LlvmValueHandle nameRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "env_get_result");
        LlvmValueHandle nameLength = LoadStringLength(state, nameRef, "env_get_name_length");
        LlvmBasicBlockHandle invalidNameBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "env_get_invalid_name");
        LlvmBasicBlockHandle lookupBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "env_get_lookup");
        LlvmBasicBlockHandle missingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "env_get_missing");
        LlvmBasicBlockHandle valueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "env_get_value");
        LlvmBasicBlockHandle validBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "env_get_valid");
        LlvmBasicBlockHandle invalidUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "env_get_invalid_utf8");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "env_get_done");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, nameLength, LlvmApi.ConstInt(state.I64, 0, 0), "env_get_empty_name"),
            invalidNameBlock, lookupBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidNameBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, "Environment variable name cannot be empty.")), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, lookupBlock);
        LlvmValueHandle value = EmitEnvironmentRawGet(state, nameRef, "env_get");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, value, LlvmApi.ConstNull(state.I8Ptr), "env_get_is_missing"),
            missingBlock, valueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, missingBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, EmitAllocAdt(state, 0, 0)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, valueBlock);
        LlvmValueHandle valueLength = EmitLinuxStrlen(state, value, "env_get_value_length");
        LlvmValueHandle utf8 = EmitValidateUtf8(state, value, valueLength, "env_get_utf8");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8, LlvmApi.ConstInt(state.I64, 0, 0), "env_get_utf8_valid"),
            validBlock, invalidUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, validBlock);
        LlvmValueHandle stringValue = EmitHeapStringSliceFromBytesPointer(state, value, valueLength, "env_get_string");
        LlvmValueHandle some = EmitAllocAdt(state, 1, 1);
        StoreAdtField(state, some, 0, stringValue, "env_get_some_value");
        LlvmApi.BuildStore(builder, EmitResultOk(state, some), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidUtf8Block);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, EnvironmentInvalidUtf8Message)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "env_get_result_value");
    }

    private static LlvmValueHandle EmitEnvironmentRawGet(LlvmCodegenState state, string name, string prefix) =>
        EmitEnvironmentRawGet(state, EmitHeapStringLiteral(state, name), prefix);

    private static LlvmValueHandle EmitEnvironmentRawGet(LlvmCodegenState state, LlvmValueHandle nameRef, string prefix)
    {
        LlvmTypeHandle functionType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmValueHandle function = EmitOrDeclareExternalFunction(state, "getenv", functionType);
        LlvmValueHandle name = EmitStringToCString(state, nameRef, prefix + "_name");
        return LlvmApi.BuildCall2(state.Target.Builder, functionType, function, [name], prefix + "_call");
    }

    private static LlvmValueHandle EmitEnvironmentUtf8Result(
        LlvmCodegenState state,
        LlvmValueHandle bytes,
        LlvmValueHandle length,
        LlvmValueHandle failed,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmBasicBlockHandle validateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_validate");
        LlvmBasicBlockHandle successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmBasicBlockHandle failureBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_failure");
        LlvmBasicBlockHandle invalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid_utf8");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmApi.BuildCondBr(builder, failed, failureBlock, validateBlock);
        LlvmApi.PositionBuilderAtEnd(builder, validateBlock);
        LlvmValueHandle valid = EmitValidateUtf8(state, bytes, length, prefix + "_utf8");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, valid, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_valid"),
            successBlock, invalidBlock);
        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmValueHandle value = EmitHeapStringSliceFromBytesPointer(state, bytes, length, prefix + "_string");
        LlvmApi.BuildStore(builder, EmitResultOk(state, value), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, failureBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, EnvironmentReadFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, invalidBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, EnvironmentInvalidUtf8Message)), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static LlvmValueHandle EmitParentPathLength(
        LlvmCodegenState state,
        LlvmValueHandle bytes,
        LlvmValueHandle length,
        string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_cursor");
        LlvmApi.BuildStore(builder, length, cursorSlot);
        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_check");
        LlvmBasicBlockHandle byteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_byte");
        LlvmBasicBlockHandle separatorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_separator");
        LlvmBasicBlockHandle decrementBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_decrement");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_done");
        LlvmApi.BuildBr(builder, checkBlock);
        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, prefix + "_cursor_value");
        LlvmApi.BuildCondBr(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, cursor, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_at_start"),
            doneBlock, byteBlock);
        LlvmApi.PositionBuilderAtEnd(builder, byteBlock);
        LlvmValueHandle index = LlvmApi.BuildSub(builder, cursor, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index");
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(builder, state.I8, bytes, [index], prefix + "_byte_ptr");
        LlvmValueHandle value = LlvmApi.BuildLoad2(builder, state.I8, bytePtr, prefix + "_byte_value");
        LlvmValueHandle slash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, value, LlvmApi.ConstInt(state.I8, (byte)'/', 0), prefix + "_slash");
        LlvmValueHandle backslash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, value, LlvmApi.ConstInt(state.I8, (byte)'\\', 0), prefix + "_backslash");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildOr(builder, slash, backslash, prefix + "_is_separator"), separatorBlock, decrementBlock);
        LlvmApi.PositionBuilderAtEnd(builder, separatorBlock);
        LlvmValueHandle parentLength = LlvmApi.BuildSelect(
            builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, index, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_root_separator"),
            LlvmApi.ConstInt(state.I64, 1, 0),
            index,
            prefix + "_parent_length");
        LlvmApi.BuildStore(builder, parentLength, cursorSlot);
        LlvmApi.BuildBr(builder, doneBlock);
        LlvmApi.PositionBuilderAtEnd(builder, decrementBlock);
        LlvmApi.BuildStore(builder, index, cursorSlot);
        LlvmApi.BuildBr(builder, checkBlock);
        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, prefix + "_length");
    }
}
