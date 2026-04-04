using LLVMSharp.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private static LLVMValueRef EmitIntComparison(LlvmCodegenState state, LLVMIntPredicate predicate, LLVMValueRef left, LLVMValueRef right, string name)
    {
        LLVMValueRef cmp = state.Target.Builder.BuildICmp(predicate, left, right, name);
        return state.Target.Builder.BuildZExt(cmp, state.I64, name + "_zext");
    }

    private static LLVMValueRef EmitFloatComparison(LlvmCodegenState state, LLVMRealPredicate predicate, LLVMValueRef left, LLVMValueRef right, string name)
    {
        LLVMValueRef cmp = state.Target.Builder.BuildFCmp(predicate, left, right, name);
        return state.Target.Builder.BuildZExt(cmp, state.I64, name + "_zext");
    }

    private static LLVMValueRef EmitInvertBool(LlvmCodegenState state, LLVMValueRef value, string name)
    {
        return state.Target.Builder.BuildXor(value, LLVMValueRef.CreateConstInt(state.I64, 1, false), name);
    }

    private static LLVMValueRef EmitMakeClosure(LlvmCodegenState state, string funcLabel, LLVMValueRef envPtr)
    {
        LLVMValueRef closurePtr = EmitAlloc(state, 16);
        LLVMValueRef codePtr = state.Target.Builder.BuildPtrToInt(state.LiftedFunctions[funcLabel], state.I64, $"closure_code_{funcLabel}");
        StoreMemory(state, closurePtr, 0, codePtr, $"closure_code_store_{funcLabel}");
        StoreMemory(state, closurePtr, 8, envPtr, $"closure_env_store_{funcLabel}");
        return closurePtr;
    }

    private static LLVMValueRef EmitCallClosure(LlvmCodegenState state, LLVMValueRef closurePtr, LLVMValueRef argValue)
    {
        LLVMValueRef codePtr = LoadMemory(state, closurePtr, 0, "closure_code");
        LLVMValueRef envPtr = LoadMemory(state, closurePtr, 8, "closure_env");
        LLVMTypeRef closureFunctionType = LLVMTypeRef.CreateFunction(state.I64, [state.I64, state.I64]);
        LLVMTypeRef closureFunctionPtrType = LLVMTypeRef.CreatePointer(closureFunctionType, 0);
        LLVMValueRef typedCodePtr = state.Target.Builder.BuildIntToPtr(codePtr, closureFunctionPtrType, "closure_code_ptr");
        return state.Target.Builder.BuildCall2(
            closureFunctionType,
            typedCodePtr,
            new[] { envPtr, argValue },
            "closure_call");
    }

    private static bool EmitJump(LlvmCodegenState state, string targetLabel)
    {
        state.Target.Builder.BuildBr(state.GetLabelBlock(targetLabel));
        return true;
    }

    private static bool EmitJumpIfFalse(LlvmCodegenState state, LLVMValueRef condValue, string targetLabel, int instructionIndex)
    {
        LLVMValueRef zero = LLVMValueRef.CreateConstInt(state.I64, 0, false);
        LLVMValueRef cond = state.Target.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, condValue, zero, $"cond_{instructionIndex}");
        LLVMBasicBlockRef target = state.GetLabelBlock(targetLabel);
        LLVMBasicBlockRef fallthrough = state.GetNextReachableBlock(instructionIndex);
        state.Target.Builder.BuildCondBr(cond, fallthrough, target);
        state.Target.Builder.PositionAtEnd(fallthrough);
        return false;
    }

    private static bool EmitReturn(LlvmCodegenState state, int source)
    {
        if (state.IsEntry)
        {
            if (state.Flavor == LlvmCodegenFlavor.Linux)
            {
                EmitExit(state, LLVMValueRef.CreateConstInt(state.I64, 0, false));
            }
            else
            {
                state.Target.Builder.BuildRetVoid();
            }
        }
        else
        {
            state.Target.Builder.BuildRet(LoadTemp(state, source));
        }

        return true;
    }

    private static bool EmitPanic(LlvmCodegenState state, LLVMValueRef stringRef)
    {
        EmitPrintStringFromTemp(state, stringRef, appendNewline: true);

        if (state.Flavor == LlvmCodegenFlavor.Linux)
        {
            EmitExit(state, LLVMValueRef.CreateConstInt(state.I64, 1, false));
        }
        else
        {
            EmitWindowsExitProcess(state, LLVMValueRef.CreateConstInt(state.I32, 1, false));
        }

        return true;
    }

    private static void EmitExit(LlvmCodegenState state, LLVMValueRef exitCode)
    {
        EmitSyscall(state, SyscallExit, exitCode, LLVMValueRef.CreateConstInt(state.I64, 0, false), LLVMValueRef.CreateConstInt(state.I64, 0, false), "sys_exit");
        state.Target.Builder.BuildUnreachable();
    }

    private static void EmitWindowsExitProcess(LlvmCodegenState state, LLVMValueRef exitCode)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef exitProcessType = LLVMTypeRef.CreateFunction(state.Target.Context.VoidType, [state.I32]);
        LLVMValueRef exitProcessPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(exitProcessType, 0),
            state.WindowsExitProcessImport,
            "exit_process_ptr");
        builder.BuildCall2(
            exitProcessType,
            exitProcessPtr,
            new[] { exitCode },
            string.Empty);
        builder.BuildUnreachable();
    }

    private static bool EmitPrintStringFromTemp(LlvmCodegenState state, LLVMValueRef stringRef, bool appendNewline)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef basePtr = builder.BuildIntToPtr(stringRef, state.I64Ptr, "str_len_ptr");
        LLVMValueRef len = builder.BuildLoad2(state.I64, basePtr, "str_len");
        LLVMValueRef byteAddress = builder.BuildAdd(stringRef, LLVMValueRef.CreateConstInt(state.I64, 8, false), "str_bytes_addr");
        LLVMValueRef bytePtr = builder.BuildIntToPtr(byteAddress, state.I8Ptr, "str_bytes_ptr");
        EmitWriteBytes(state, bytePtr, len);
        if (appendNewline)
        {
            EmitWriteBytes(state, EmitStackByteArray(state, [10]), LLVMValueRef.CreateConstInt(state.I64, 1, false));
        }

        return false;
    }

    private static bool EmitPrintBool(LlvmCodegenState state, LLVMValueRef boolValue)
    {
        LLVMValueRef zero = LLVMValueRef.CreateConstInt(state.I64, 0, false);
        LLVMValueRef isTrue = state.Target.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, boolValue, zero, "bool_is_true");
        EmitConditionalWrite(state, isTrue, "true", "false", appendNewline: true);
        return false;
    }
}
