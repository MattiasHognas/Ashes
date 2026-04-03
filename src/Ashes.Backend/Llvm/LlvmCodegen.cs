using System.Buffers.Binary;
using Ashes.Backend.Backends;
using Ashes.Semantics;
using LLVMSharp.Interop;

namespace Ashes.Backend.Llvm;

internal static class LlvmCodegen
{
    private const long SyscallWrite = 1;
    private const long SyscallExit = 60;

    public static byte[] Compile(IrProgram program, string targetId, BackendCompileOptions options)
    {
        return targetId switch
        {
            Backends.TargetIds.LinuxX64 => CompileLinux(program, options),
            Backends.TargetIds.WindowsX64 => CompileWindows(program, options),
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unknown target '{targetId}'."),
        };
    }

    private static byte[] CompileWindows(IrProgram program, BackendCompileOptions options)
    {
        if (!SupportsMinimalWindowsLlvm(program))
        {
            return new Pe64Writer().CompileToPe(program);
        }

        using LlvmTargetContext target = LlvmTargetSetup.Create(Backends.TargetIds.WindowsX64, options.OptimizationLevel);
        var literals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        EmitEntryModule(target, program.EntryFunction, literals, "entry", LlvmCodegenFlavor.Windows);

        if (!target.Module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out string verifyError))
        {
            throw new InvalidOperationException($"LLVM module verification failed: {verifyError}");
        }

        string objectPath = Path.Combine(Path.GetTempPath(), $"ashes-llvm-{Guid.NewGuid():N}.obj");
        target.TargetMachine.EmitToFile(target.Module, objectPath, LLVMCodeGenFileType.LLVMObjectFile);
        try
        {
            byte[] objectBytes = File.ReadAllBytes(objectPath);
            return LlvmImageLinker.LinkWindowsExecutable(objectBytes, "entry");
        }
        finally
        {
            try
            {
                File.Delete(objectPath);
            }
            catch
            {
            }
        }
    }

    private static byte[] CompileLinux(IrProgram program, BackendCompileOptions options)
    {
        if (program.Functions.Count != 0 || program.UsesClosures)
        {
            throw new InvalidOperationException("The LLVM Linux backend does not yet support closures or lowered lambda functions.");
        }

        using LlvmTargetContext target = LlvmTargetSetup.Create(Backends.TargetIds.LinuxX64, options.OptimizationLevel);
        var literals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        EmitEntryModule(target, program.EntryFunction, literals, "_start", LlvmCodegenFlavor.Linux);

        if (!target.Module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out string verifyError))
        {
            throw new InvalidOperationException($"LLVM module verification failed: {verifyError}");
        }

        string objectPath = Path.Combine(Path.GetTempPath(), $"ashes-llvm-{Guid.NewGuid():N}.o");
        target.TargetMachine.EmitToFile(target.Module, objectPath, LLVMCodeGenFileType.LLVMObjectFile);
        try
        {
            byte[] objectBytes = File.ReadAllBytes(objectPath);
            return LlvmImageLinker.LinkLinuxExecutable(objectBytes);
        }
        finally
        {
            try
            {
                File.Delete(objectPath);
            }
            catch
            {
            }
        }
    }

    private static void EmitEntryModule(
        LlvmTargetContext target,
        IrFunction function,
        IReadOnlyDictionary<string, string> stringLiterals,
        string functionName,
        LlvmCodegenFlavor flavor)
    {
        LLVMTypeRef i64 = target.Context.Int64Type;
        LLVMTypeRef i8 = target.Context.Int8Type;
        LLVMTypeRef voidType = target.Context.VoidType;
        LLVMTypeRef i8Ptr = LLVMTypeRef.CreatePointer(i8, 0);
        LLVMTypeRef i64Ptr = LLVMTypeRef.CreatePointer(i64, 0);

        LLVMTypeRef functionType = LLVMTypeRef.CreateFunction(voidType, []);
        LLVMValueRef llvmFunction = target.Module.AddFunction(functionName, functionType);
        llvmFunction.Linkage = LLVMLinkage.LLVMExternalLinkage;

        LLVMBasicBlockRef entryBlock = llvmFunction.AppendBasicBlock("entry");
        target.Builder.PositionAtEnd(entryBlock);

        var tempSlots = new LLVMValueRef[function.TempCount];
        for (int i = 0; i < tempSlots.Length; i++)
        {
            tempSlots[i] = target.Builder.BuildAlloca(i64, $"tmp_{i}");
            target.Builder.BuildStore(LLVMValueRef.CreateConstInt(i64, 0, false), tempSlots[i]);
        }

        var localSlots = new LLVMValueRef[function.LocalCount];
        for (int i = 0; i < localSlots.Length; i++)
        {
            localSlots[i] = target.Builder.BuildAlloca(i64, $"local_{i}");
            target.Builder.BuildStore(LLVMValueRef.CreateConstInt(i64, 0, false), localSlots[i]);
        }

        var labelBlocks = new Dictionary<string, LLVMBasicBlockRef>(StringComparer.Ordinal);
        foreach (IrInst.Label label in function.Instructions.OfType<IrInst.Label>())
        {
            labelBlocks[label.Name] = llvmFunction.AppendBasicBlock(label.Name);
        }

        var fallthroughBlocks = new Dictionary<int, LLVMBasicBlockRef>();
        var state = new LlvmCodegenState(
            target,
            llvmFunction,
            stringLiterals,
            tempSlots,
            localSlots,
            labelBlocks,
            fallthroughBlocks,
            i64,
            i8,
            i8Ptr,
            i64Ptr,
            flavor);

        LLVMBasicBlockRef currentBlock = entryBlock;
        bool terminated = false;
        for (int index = 0; index < function.Instructions.Count; index++)
        {
            IrInst instruction = function.Instructions[index];
            if (instruction is IrInst.Label label)
            {
                if (!terminated)
                {
                    target.Builder.BuildBr(state.GetLabelBlock(label.Name));
                }

                currentBlock = state.GetLabelBlock(label.Name);
                target.Builder.PositionAtEnd(currentBlock);
                terminated = false;
                continue;
            }

            if (terminated)
            {
                currentBlock = state.GetOrCreateFallthroughBlock(index);
                target.Builder.PositionAtEnd(currentBlock);
                terminated = false;
            }

            terminated = EmitInstruction(state, instruction, index);
        }

        if (!terminated)
        {
            if (state.Flavor == LlvmCodegenFlavor.Linux)
            {
                EmitExit(state, LLVMValueRef.CreateConstInt(i64, 0, false));
            }
            else
            {
                target.Builder.BuildRetVoid();
            }
        }
    }

    private static bool EmitInstruction(LlvmCodegenState state, IrInst instruction, int index)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        return instruction switch
        {
            IrInst.LoadConstInt loadConstInt => StoreTemp(state, loadConstInt.Target, LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)loadConstInt.Value), true)),
            IrInst.LoadConstBool loadConstBool => StoreTemp(state, loadConstBool.Target, LLVMValueRef.CreateConstInt(state.I64, loadConstBool.Value ? 1UL : 0UL, false)),
            IrInst.LoadConstStr loadConstStr => StoreTemp(state, loadConstStr.Target, EmitStackStringObject(state, state.StringLiterals[loadConstStr.StrLabel])),
            IrInst.LoadLocal loadLocal => StoreTemp(state, loadLocal.Target, builder.BuildLoad2(state.I64, state.LocalSlots[loadLocal.Slot], $"load_local_{loadLocal.Slot}")),
            IrInst.StoreLocal storeLocal => StoreLocal(state, storeLocal.Slot, LoadTemp(state, storeLocal.Source)),
            IrInst.AddInt addInt => StoreTemp(state, addInt.Target, builder.BuildAdd(LoadTemp(state, addInt.Left), LoadTemp(state, addInt.Right), $"add_{addInt.Target}")),
            IrInst.SubInt subInt => StoreTemp(state, subInt.Target, builder.BuildSub(LoadTemp(state, subInt.Left), LoadTemp(state, subInt.Right), $"sub_{subInt.Target}")),
            IrInst.MulInt mulInt => StoreTemp(state, mulInt.Target, builder.BuildMul(LoadTemp(state, mulInt.Left), LoadTemp(state, mulInt.Right), $"mul_{mulInt.Target}")),
            IrInst.DivInt divInt => StoreTemp(state, divInt.Target, builder.BuildSDiv(LoadTemp(state, divInt.Left), LoadTemp(state, divInt.Right), $"div_{divInt.Target}")),
            IrInst.CmpIntGe cmpIntGe => StoreTemp(state, cmpIntGe.Target, EmitIntComparison(state, LLVMIntPredicate.LLVMIntSGE, LoadTemp(state, cmpIntGe.Left), LoadTemp(state, cmpIntGe.Right), $"cmp_ge_{cmpIntGe.Target}")),
            IrInst.CmpIntLe cmpIntLe => StoreTemp(state, cmpIntLe.Target, EmitIntComparison(state, LLVMIntPredicate.LLVMIntSLE, LoadTemp(state, cmpIntLe.Left), LoadTemp(state, cmpIntLe.Right), $"cmp_le_{cmpIntLe.Target}")),
            IrInst.CmpIntEq cmpIntEq => StoreTemp(state, cmpIntEq.Target, EmitIntComparison(state, LLVMIntPredicate.LLVMIntEQ, LoadTemp(state, cmpIntEq.Left), LoadTemp(state, cmpIntEq.Right), $"cmp_eq_{cmpIntEq.Target}")),
            IrInst.CmpIntNe cmpIntNe => StoreTemp(state, cmpIntNe.Target, EmitIntComparison(state, LLVMIntPredicate.LLVMIntNE, LoadTemp(state, cmpIntNe.Left), LoadTemp(state, cmpIntNe.Right), $"cmp_ne_{cmpIntNe.Target}")),
            IrInst.PrintInt printInt => state.Flavor == LlvmCodegenFlavor.Linux ? EmitPrintInt(state, LoadTemp(state, printInt.Source)) : ThrowWindowsInstructionNotSupported(printInt),
            IrInst.PrintStr printStr => state.Flavor == LlvmCodegenFlavor.Linux ? EmitPrintStringFromTemp(state, LoadTemp(state, printStr.Source), appendNewline: true) : ThrowWindowsInstructionNotSupported(printStr),
            IrInst.WriteStr writeStr => state.Flavor == LlvmCodegenFlavor.Linux ? EmitPrintStringFromTemp(state, LoadTemp(state, writeStr.Source), appendNewline: false) : ThrowWindowsInstructionNotSupported(writeStr),
            IrInst.PrintBool printBool => state.Flavor == LlvmCodegenFlavor.Linux ? EmitPrintBool(state, LoadTemp(state, printBool.Source)) : ThrowWindowsInstructionNotSupported(printBool),
            IrInst.AllocAdt allocAdt when allocAdt.FieldCount == 0 => StoreTemp(state, allocAdt.Target, LLVMValueRef.CreateConstInt(state.I64, 0, false)),
            IrInst.Jump jump => EmitJump(state, jump.Target),
            IrInst.JumpIfFalse jumpIfFalse => EmitJumpIfFalse(state, LoadTemp(state, jumpIfFalse.CondTemp), jumpIfFalse.Target, index),
            IrInst.Return => state.Flavor == LlvmCodegenFlavor.Linux ? EmitReturn(state) : EmitReturnVoid(state),
            _ => throw new InvalidOperationException($"The LLVM Linux backend does not yet support instruction '{instruction.GetType().Name}'.")
        };
    }

    private static bool StoreTemp(LlvmCodegenState state, int target, LLVMValueRef value)
    {
        state.Target.Builder.BuildStore(NormalizeToI64(state, value), state.TempSlots[target]);
        return false;
    }

    private static bool StoreLocal(LlvmCodegenState state, int slot, LLVMValueRef value)
    {
        state.Target.Builder.BuildStore(NormalizeToI64(state, value), state.LocalSlots[slot]);
        return false;
    }

    private static LLVMValueRef LoadTemp(LlvmCodegenState state, int temp)
    {
        return state.Target.Builder.BuildLoad2(state.I64, state.TempSlots[temp], $"tmpv_{temp}");
    }

    private static LLVMValueRef NormalizeToI64(LlvmCodegenState state, LLVMValueRef value)
    {
        return value.TypeOf.Kind switch
        {
            LLVMTypeKind.LLVMIntegerTypeKind when value.TypeOf.IntWidth == 64 => value,
            LLVMTypeKind.LLVMIntegerTypeKind => state.Target.Builder.BuildZExt(value, state.I64, "zext_i64"),
            LLVMTypeKind.LLVMPointerTypeKind => state.Target.Builder.BuildPtrToInt(value, state.I64, "ptr_i64"),
            _ => throw new InvalidOperationException($"Cannot normalize LLVM value of type '{value.TypeOf.Kind}' to i64.")
        };
    }

    private static LLVMValueRef EmitIntComparison(LlvmCodegenState state, LLVMIntPredicate predicate, LLVMValueRef left, LLVMValueRef right, string name)
    {
        LLVMValueRef cmp = state.Target.Builder.BuildICmp(predicate, left, right, name);
        return state.Target.Builder.BuildZExt(cmp, state.I64, name + "_zext");
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

    private static bool EmitReturn(LlvmCodegenState state)
    {
        EmitExit(state, LLVMValueRef.CreateConstInt(state.I64, 0, false));
        return true;
    }

    private static bool EmitReturnVoid(LlvmCodegenState state)
    {
        state.Target.Builder.BuildRetVoid();
        return true;
    }

    private static void EmitExit(LlvmCodegenState state, LLVMValueRef exitCode)
    {
        EmitSyscall(state, SyscallExit, exitCode, LLVMValueRef.CreateConstInt(state.I64, 0, false), LLVMValueRef.CreateConstInt(state.I64, 0, false), "sys_exit");
        state.Target.Builder.BuildUnreachable();
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

    private static bool EmitPrintInt(LlvmCodegenState state, LLVMValueRef value)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, "print_idx");
        LLVMValueRef workSlot = builder.BuildAlloca(state.I64, "print_work");
        LLVMValueRef negativeSlot = builder.BuildAlloca(state.I64, "print_negative");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);

        LLVMTypeRef bufferType = LLVMTypeRef.CreateArray(state.I8, 32);
        LLVMValueRef buffer = builder.BuildAlloca(bufferType, "print_buf");

        LLVMValueRef zero = LLVMValueRef.CreateConstInt(state.I64, 0, false);
        LLVMValueRef isNegative = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, value, zero, "is_negative");
        LLVMValueRef negativeValue = builder.BuildZExt(isNegative, state.I64, "negative_i64");
        builder.BuildStore(negativeValue, negativeSlot);
        LLVMValueRef absValue = builder.BuildSelect(isNegative, builder.BuildSub(zero, value, "negated_value"), value, "abs_value");
        builder.BuildStore(absValue, workSlot);

        var zeroBlock = state.Function.AppendBasicBlock("print_int_zero");
        var loopCheckBlock = state.Function.AppendBasicBlock("print_int_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock("print_int_loop_body");
        var maybeSignBlock = state.Function.AppendBasicBlock("print_int_maybe_sign");
        var signBlock = state.Function.AppendBasicBlock("print_int_sign");
        var writeBlock = state.Function.AppendBasicBlock("print_int_write");
        var continueBlock = state.Function.AppendBasicBlock("print_int_continue");

        LLVMValueRef isZero = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, absValue, zero, "is_zero");
        builder.BuildCondBr(isZero, zeroBlock, loopCheckBlock);

        builder.PositionAtEnd(zeroBlock);
        StoreBufferByte(state, buffer, LLVMValueRef.CreateConstInt(state.I64, 31, false), (byte)'0');
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 1, false), indexSlot);
        builder.BuildBr(writeBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef work = builder.BuildLoad2(state.I64, workSlot, "work_value");
        LLVMValueRef loopDone = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, work, zero, "loop_done");
        builder.BuildCondBr(loopDone, maybeSignBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef digit = builder.BuildSRem(work, LLVMValueRef.CreateConstInt(state.I64, 10, false), "digit");
        LLVMValueRef nextWork = builder.BuildSDiv(work, LLVMValueRef.CreateConstInt(state.I64, 10, false), "next_work");
        builder.BuildStore(nextWork, workSlot);
        LLVMValueRef idx = builder.BuildLoad2(state.I64, indexSlot, "digit_idx");
        LLVMValueRef writeIndex = builder.BuildSub(LLVMValueRef.CreateConstInt(state.I64, 31, false), idx, "digit_write_index");
        LLVMValueRef asciiDigit = builder.BuildAdd(digit, LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), "ascii_digit");
        StoreBufferByte(state, buffer, writeIndex, asciiDigit);
        builder.BuildStore(builder.BuildAdd(idx, LLVMValueRef.CreateConstInt(state.I64, 1, false), "idx_inc"), indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(maybeSignBlock);
        LLVMValueRef negative = builder.BuildLoad2(state.I64, negativeSlot, "negative_value");
        LLVMValueRef hasSign = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, negative, zero, "has_sign");
        builder.BuildCondBr(hasSign, signBlock, writeBlock);

        builder.PositionAtEnd(signBlock);
        LLVMValueRef idxBeforeSign = builder.BuildLoad2(state.I64, indexSlot, "idx_before_sign");
        LLVMValueRef signIndex = builder.BuildSub(LLVMValueRef.CreateConstInt(state.I64, 31, false), idxBeforeSign, "sign_index");
        StoreBufferByte(state, buffer, signIndex, (byte)'-');
        builder.BuildStore(builder.BuildAdd(idxBeforeSign, LLVMValueRef.CreateConstInt(state.I64, 1, false), "idx_with_sign"), indexSlot);
        builder.BuildBr(writeBlock);

        builder.PositionAtEnd(writeBlock);
        LLVMValueRef count = builder.BuildLoad2(state.I64, indexSlot, "print_count");
        LLVMValueRef startIndex = builder.BuildSub(LLVMValueRef.CreateConstInt(state.I64, 32, false), count, "start_index");
        LLVMValueRef dataPtr = GetArrayElementPointer(state, bufferType, buffer, startIndex, "print_data_ptr");
        EmitWriteBytes(state, dataPtr, count);
        EmitWriteBytes(state, EmitStackByteArray(state, [10]), LLVMValueRef.CreateConstInt(state.I64, 1, false));
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return false;
    }

    private static void EmitConditionalWrite(LlvmCodegenState state, LLVMValueRef condition, string whenTrue, string whenFalse, bool appendNewline)
    {
        var trueBlock = state.Function.AppendBasicBlock("bool_true");
        var falseBlock = state.Function.AppendBasicBlock("bool_false");
        var continueBlock = state.Function.AppendBasicBlock("bool_continue");
        state.Target.Builder.BuildCondBr(condition, trueBlock, falseBlock);

        state.Target.Builder.PositionAtEnd(trueBlock);
        EmitWriteBytes(state, EmitStackByteArray(state, System.Text.Encoding.UTF8.GetBytes(whenTrue)), LLVMValueRef.CreateConstInt(state.I64, (ulong)whenTrue.Length, false));
        if (appendNewline)
        {
            EmitWriteBytes(state, EmitStackByteArray(state, [10]), LLVMValueRef.CreateConstInt(state.I64, 1, false));
        }
        state.Target.Builder.BuildBr(continueBlock);

        state.Target.Builder.PositionAtEnd(falseBlock);
        EmitWriteBytes(state, EmitStackByteArray(state, System.Text.Encoding.UTF8.GetBytes(whenFalse)), LLVMValueRef.CreateConstInt(state.I64, (ulong)whenFalse.Length, false));
        if (appendNewline)
        {
            EmitWriteBytes(state, EmitStackByteArray(state, [10]), LLVMValueRef.CreateConstInt(state.I64, 1, false));
        }
        state.Target.Builder.BuildBr(continueBlock);

        state.Target.Builder.PositionAtEnd(continueBlock);
    }

    private static LLVMValueRef EmitStackStringObject(LlvmCodegenState state, string value)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);
        LLVMTypeRef objectType = LLVMTypeRef.CreateArray(state.I8, (uint)(utf8.Length + 8));
        LLVMValueRef storage = state.Target.Builder.BuildAlloca(objectType, "str_obj");
        LLVMValueRef lenPtr = state.Target.Builder.BuildBitCast(storage, state.I64Ptr, "str_obj_len_ptr");
        state.Target.Builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, (ulong)utf8.Length, false), lenPtr);
        LLVMValueRef bytesPtr = GetArrayElementPointer(state, objectType, storage, LLVMValueRef.CreateConstInt(state.I64, 8, false), "str_obj_bytes");
        for (int i = 0; i < utf8.Length; i++)
        {
            LLVMValueRef cellPtr = state.Target.Builder.BuildGEP2(
                state.I8,
                bytesPtr,
                new[]
                {
                    LLVMValueRef.CreateConstInt(state.I64, (ulong)i, false)
                },
                $"str_byte_ptr_{i}");
            state.Target.Builder.BuildStore(LLVMValueRef.CreateConstInt(state.I8, utf8[i], false), cellPtr);
        }

        return state.Target.Builder.BuildPtrToInt(storage, state.I64, "str_obj_i64");
    }

    private static LLVMValueRef EmitStackByteArray(LlvmCodegenState state, IReadOnlyList<byte> bytes)
    {
        LLVMTypeRef arrayType = LLVMTypeRef.CreateArray(state.I8, (uint)bytes.Count);
        LLVMValueRef storage = state.Target.Builder.BuildAlloca(arrayType, "byte_array");
        for (int i = 0; i < bytes.Count; i++)
        {
            LLVMValueRef ptr = GetArrayElementPointer(state, arrayType, storage, LLVMValueRef.CreateConstInt(state.I64, (ulong)i, false), $"byte_{i}");
            state.Target.Builder.BuildStore(LLVMValueRef.CreateConstInt(state.I8, bytes[i], false), ptr);
        }

        return GetArrayElementPointer(state, arrayType, storage, LLVMValueRef.CreateConstInt(state.I64, 0, false), "byte_array_ptr");
    }

    private static void StoreBufferByte(LlvmCodegenState state, LLVMValueRef buffer, LLVMValueRef index, byte value)
    {
        StoreBufferByte(state, buffer, index, LLVMValueRef.CreateConstInt(state.I64, value, false));
    }

    private static void StoreBufferByte(LlvmCodegenState state, LLVMValueRef buffer, LLVMValueRef index, LLVMValueRef value)
    {
        LLVMValueRef ptr = GetArrayElementPointer(state, LLVMTypeRef.CreateArray(state.I8, 32), buffer, index, "buf_ptr");
        LLVMValueRef byteValue = value.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind && value.TypeOf.IntWidth == 8
            ? value
            : state.Target.Builder.BuildTrunc(value, state.I8, "to_i8");
        state.Target.Builder.BuildStore(byteValue, ptr);
    }

    private static LLVMValueRef GetArrayElementPointer(LlvmCodegenState state, LLVMTypeRef arrayType, LLVMValueRef storage, LLVMValueRef index, string name)
    {
        return state.Target.Builder.BuildGEP2(
            arrayType,
            storage,
            new[]
            {
                LLVMValueRef.CreateConstInt(state.I64, 0, false),
                index
            },
            name);
    }

    private static void EmitWriteBytes(LlvmCodegenState state, LLVMValueRef bytePtr, LLVMValueRef len)
    {
        EmitSyscall(
            state,
            SyscallWrite,
            LLVMValueRef.CreateConstInt(state.I64, 1, false),
            state.Target.Builder.BuildPtrToInt(bytePtr, state.I64, "write_ptr_i64"),
            len,
            "sys_write");
    }

    private static LLVMValueRef EmitSyscall(LlvmCodegenState state, long nr, LLVMValueRef arg1, LLVMValueRef arg2, LLVMValueRef arg3, string name)
    {
        LLVMTypeRef syscallType = LLVMTypeRef.CreateFunction(state.I64, [state.I64, state.I64, state.I64, state.I64]);
        LLVMValueRef syscall = LLVMValueRef.CreateConstInlineAsm(
            syscallType,
            "syscall",
            "={rax},{rax},{rdi},{rsi},{rdx},~{rcx},~{r11},~{memory}",
            true,
            false);
        return state.Target.Builder.BuildCall2(
            syscallType,
            syscall,
            new[]
            {
                LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)nr), true),
                NormalizeToI64(state, arg1),
                NormalizeToI64(state, arg2),
                NormalizeToI64(state, arg3)
            },
            name);
    }

    private sealed record LlvmCodegenState(
        LlvmTargetContext Target,
        LLVMValueRef Function,
        IReadOnlyDictionary<string, string> StringLiterals,
        LLVMValueRef[] TempSlots,
        LLVMValueRef[] LocalSlots,
        Dictionary<string, LLVMBasicBlockRef> LabelBlocks,
        Dictionary<int, LLVMBasicBlockRef> FallthroughBlocks,
        LLVMTypeRef I64,
        LLVMTypeRef I8,
        LLVMTypeRef I8Ptr,
        LLVMTypeRef I64Ptr,
        LlvmCodegenFlavor Flavor)
    {
        public LLVMBasicBlockRef GetLabelBlock(string name) => LabelBlocks[name];

        public LLVMBasicBlockRef GetOrCreateFallthroughBlock(int instructionIndex)
        {
            if (!FallthroughBlocks.TryGetValue(instructionIndex, out LLVMBasicBlockRef block))
            {
                block = Function.AppendBasicBlock($"bb_{instructionIndex}");
                FallthroughBlocks[instructionIndex] = block;
            }

            return block;
        }

        public LLVMBasicBlockRef GetNextReachableBlock(int instructionIndex)
        {
            int nextIndex = instructionIndex + 1;
            return FallthroughBlocks.TryGetValue(nextIndex, out LLVMBasicBlockRef block)
                ? block
                : GetOrCreateFallthroughBlock(nextIndex);
        }
    }

    private static bool SupportsMinimalWindowsLlvm(IrProgram program)
    {
        if (program.Functions.Count != 0 || program.UsesClosures)
        {
            return false;
        }

        foreach (IrInst instruction in program.EntryFunction.Instructions)
        {
            switch (instruction)
            {
                case IrInst.LoadConstInt:
                case IrInst.LoadConstBool:
                case IrInst.LoadConstStr:
                case IrInst.LoadLocal:
                case IrInst.StoreLocal:
                case IrInst.AddInt:
                case IrInst.SubInt:
                case IrInst.MulInt:
                case IrInst.DivInt:
                case IrInst.CmpIntGe:
                case IrInst.CmpIntLe:
                case IrInst.CmpIntEq:
                case IrInst.CmpIntNe:
                case IrInst.AllocAdt { FieldCount: 0 }:
                case IrInst.Jump:
                case IrInst.JumpIfFalse:
                case IrInst.Return:
                case IrInst.Label:
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool ThrowWindowsInstructionNotSupported(IrInst instruction)
    {
        throw new InvalidOperationException(
            $"The minimal Windows LLVM path does not yet support instruction '{instruction.GetType().Name}'.");
    }

    private enum LlvmCodegenFlavor
    {
        Linux,
        Windows
    }
}
