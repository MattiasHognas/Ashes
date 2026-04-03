using System.Buffers.Binary;
using Ashes.Backend.Backends;
using Ashes.Semantics;
using LLVMSharp.Interop;

namespace Ashes.Backend.Llvm;

internal static class LlvmCodegen
{
    private const int HeapSizeBytes = 2048;
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
        EmitProgramModule(target, program, "entry", LlvmCodegenFlavor.Windows);

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
        if (!SupportsMinimalLinuxLlvm(program))
        {
            return new X64CodegenIced().CompileToElf(program);
        }

        using LlvmTargetContext target = LlvmTargetSetup.Create(Backends.TargetIds.LinuxX64, options.OptimizationLevel);
        var literals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        EmitProgramModule(target, program, "entry", LlvmCodegenFlavor.Linux);

        if (!target.Module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out string verifyError))
        {
            throw new InvalidOperationException($"LLVM module verification failed: {verifyError}");
        }

        string objectPath = Path.Combine(Path.GetTempPath(), $"ashes-llvm-{Guid.NewGuid():N}.o");
        target.TargetMachine.EmitToFile(target.Module, objectPath, LLVMCodeGenFileType.LLVMObjectFile);
        try
        {
            byte[] objectBytes = File.ReadAllBytes(objectPath);
            return LlvmImageLinker.LinkLinuxExecutable(objectBytes, "entry");
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

    private static void EmitProgramModule(
        LlvmTargetContext target,
        IrProgram program,
        string entryFunctionName,
        LlvmCodegenFlavor flavor)
    {
        LLVMTypeRef i64 = target.Context.Int64Type;
        LLVMTypeRef i8 = target.Context.Int8Type;
        LLVMTypeRef f64 = target.Context.DoubleType;
        LLVMTypeRef voidType = target.Context.VoidType;
        LLVMTypeRef i8Ptr = LLVMTypeRef.CreatePointer(i8, 0);
        LLVMTypeRef i64Ptr = LLVMTypeRef.CreatePointer(i64, 0);
        var stringLiterals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        LLVMTypeRef closureFunctionType = LLVMTypeRef.CreateFunction(i64, [i64, i64]);
        bool usesProgramArgs = ProgramUsesInstruction<IrInst.LoadProgramArgs>(program);

        LLVMValueRef entryFunction = target.Module.AddFunction(
            entryFunctionName,
            flavor == LlvmCodegenFlavor.Linux
                ? LLVMTypeRef.CreateFunction(voidType, [i64])
                : LLVMTypeRef.CreateFunction(voidType, []));
        entryFunction.Linkage = LLVMLinkage.LLVMExternalLinkage;

        var liftedFunctions = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        foreach (IrFunction function in program.Functions)
        {
            LLVMValueRef llvmFunction = target.Module.AddFunction(function.Label, closureFunctionType);
            llvmFunction.Linkage = LLVMLinkage.LLVMInternalLinkage;
            liftedFunctions.Add(function.Label, llvmFunction);
        }

        EmitFunctionBody(
            target,
            entryFunction,
            program.EntryFunction,
            stringLiterals,
            liftedFunctions,
            flavor,
            usesProgramArgs,
            isEntry: true);

        foreach (IrFunction function in program.Functions)
        {
            EmitFunctionBody(
                target,
                liftedFunctions[function.Label],
                function,
                stringLiterals,
                liftedFunctions,
                flavor,
                usesProgramArgs,
                isEntry: false);
        }
    }

    private static bool ProgramUsesInstruction<TInstruction>(IrProgram program)
        where TInstruction : IrInst
    {
        return program.EntryFunction.Instructions.Any(static instruction => instruction is TInstruction)
            || program.Functions.Any(static function => function.Instructions.Any(static instruction => instruction is TInstruction));
    }

    private static bool RequiresEntryHeapStorage(IrInst instruction)
    {
        return instruction is IrInst.Alloc or IrInst.AllocAdt or IrInst.ConcatStr or IrInst.MakeClosure or IrInst.LoadProgramArgs;
    }

    private static void EmitFunctionBody(
        LlvmTargetContext target,
        LLVMValueRef llvmFunction,
        IrFunction function,
        IReadOnlyDictionary<string, string> stringLiterals,
        IReadOnlyDictionary<string, LLVMValueRef> liftedFunctions,
        LlvmCodegenFlavor flavor,
        bool usesProgramArgs,
        bool isEntry)
    {
        LLVMTypeRef i64 = target.Context.Int64Type;
        LLVMTypeRef i8 = target.Context.Int8Type;
        LLVMTypeRef f64 = target.Context.DoubleType;
        LLVMTypeRef i8Ptr = LLVMTypeRef.CreatePointer(i8, 0);
        LLVMTypeRef i64Ptr = LLVMTypeRef.CreatePointer(i64, 0);

        LLVMBasicBlockRef entryBlock = llvmFunction.AppendBasicBlock("entry");
        target.Builder.PositionAtEnd(entryBlock);

        LLVMValueRef entryStackPointer = isEntry && flavor == LlvmCodegenFlavor.Linux
            ? llvmFunction.GetParam(0)
            : default;

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

        LLVMValueRef programArgsSlot = target.Builder.BuildAlloca(i64, "program_args");
        target.Builder.BuildStore(LLVMValueRef.CreateConstInt(i64, 0, false), programArgsSlot);

        LLVMValueRef heapCursorSlot = target.Builder.BuildAlloca(i64, "heap_cursor");
        bool needsHeap = function.Instructions.Any(RequiresEntryHeapStorage);
        if (needsHeap)
        {
            LLVMTypeRef heapType = LLVMTypeRef.CreateArray(i8, HeapSizeBytes);
            LLVMValueRef heapStorage = target.Builder.BuildAlloca(heapType, "heap");
            LLVMValueRef heapBasePtr = target.Builder.BuildGEP2(
                heapType,
                heapStorage,
                new[]
                {
                    LLVMValueRef.CreateConstInt(i64, 0, false),
                    LLVMValueRef.CreateConstInt(i64, 0, false)
                },
                "heap_base_ptr");
            target.Builder.BuildStore(target.Builder.BuildPtrToInt(heapBasePtr, i64, "heap_base_i64"), heapCursorSlot);
        }
        else
        {
            target.Builder.BuildStore(LLVMValueRef.CreateConstInt(i64, 0, false), heapCursorSlot);
        }

        if (!isEntry && function.HasEnvAndArgParams)
        {
            target.Builder.BuildStore(llvmFunction.GetParam(0), localSlots[0]);
            target.Builder.BuildStore(llvmFunction.GetParam(1), localSlots[1]);
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
            liftedFunctions,
            programArgsSlot,
            tempSlots,
            localSlots,
            heapCursorSlot,
            labelBlocks,
            fallthroughBlocks,
            i64,
            i8,
            f64,
            i8Ptr,
            i64Ptr,
            entryStackPointer,
            flavor,
            usesProgramArgs,
            isEntry);

        if (isEntry && usesProgramArgs)
        {
            EmitEntryProgramArgsInitialization(state);
        }

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

                target.Builder.PositionAtEnd(state.GetLabelBlock(label.Name));
                terminated = false;
                continue;
            }

            if (terminated)
            {
                target.Builder.PositionAtEnd(state.GetOrCreateFallthroughBlock(index));
                terminated = false;
            }

            terminated = EmitInstruction(state, instruction, index);
        }

        if (!terminated)
        {
            if (state.IsEntry)
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
            else
            {
                target.Builder.BuildRet(LLVMValueRef.CreateConstInt(i64, 0, false));
            }
        }
    }

    private static bool EmitInstruction(LlvmCodegenState state, IrInst instruction, int index)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        return instruction switch
        {
            IrInst.LoadConstInt loadConstInt => StoreTemp(state, loadConstInt.Target, LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)loadConstInt.Value), true)),
            IrInst.LoadConstFloat loadConstFloat => StoreTemp(state, loadConstFloat.Target, LLVMValueRef.CreateConstReal(state.F64, loadConstFloat.Value)),
            IrInst.LoadConstBool loadConstBool => StoreTemp(state, loadConstBool.Target, LLVMValueRef.CreateConstInt(state.I64, loadConstBool.Value ? 1UL : 0UL, false)),
            IrInst.LoadConstStr loadConstStr => StoreTemp(state, loadConstStr.Target, EmitStackStringObject(state, state.StringLiterals[loadConstStr.StrLabel])),
            IrInst.LoadProgramArgs loadProgramArgs => StoreTemp(state, loadProgramArgs.Target, builder.BuildLoad2(state.I64, state.ProgramArgsSlot, "program_args")),
            IrInst.LoadLocal loadLocal => StoreTemp(state, loadLocal.Target, builder.BuildLoad2(state.I64, state.LocalSlots[loadLocal.Slot], $"load_local_{loadLocal.Slot}")),
            IrInst.StoreLocal storeLocal => StoreLocal(state, storeLocal.Slot, LoadTemp(state, storeLocal.Source)),
            IrInst.LoadEnv loadEnv => StoreTemp(state, loadEnv.Target, builder.BuildLoad2(state.I64, GetMemoryPointer(state, builder.BuildLoad2(state.I64, state.LocalSlots[0], "env_ptr"), loadEnv.Index * 8, $"load_env_{loadEnv.Index}_ptr"), $"load_env_{loadEnv.Index}")),
            IrInst.Alloc alloc => StoreTemp(state, alloc.Target, EmitAlloc(state, alloc.SizeBytes)),
            IrInst.AddInt addInt => StoreTemp(state, addInt.Target, builder.BuildAdd(LoadTemp(state, addInt.Left), LoadTemp(state, addInt.Right), $"add_{addInt.Target}")),
            IrInst.AddFloat addFloat => StoreTemp(state, addFloat.Target, builder.BuildFAdd(LoadTempAsFloat(state, addFloat.Left), LoadTempAsFloat(state, addFloat.Right), $"fadd_{addFloat.Target}")),
            IrInst.SubInt subInt => StoreTemp(state, subInt.Target, builder.BuildSub(LoadTemp(state, subInt.Left), LoadTemp(state, subInt.Right), $"sub_{subInt.Target}")),
            IrInst.SubFloat subFloat => StoreTemp(state, subFloat.Target, builder.BuildFSub(LoadTempAsFloat(state, subFloat.Left), LoadTempAsFloat(state, subFloat.Right), $"fsub_{subFloat.Target}")),
            IrInst.MulInt mulInt => StoreTemp(state, mulInt.Target, builder.BuildMul(LoadTemp(state, mulInt.Left), LoadTemp(state, mulInt.Right), $"mul_{mulInt.Target}")),
            IrInst.MulFloat mulFloat => StoreTemp(state, mulFloat.Target, builder.BuildFMul(LoadTempAsFloat(state, mulFloat.Left), LoadTempAsFloat(state, mulFloat.Right), $"fmul_{mulFloat.Target}")),
            IrInst.DivInt divInt => StoreTemp(state, divInt.Target, builder.BuildSDiv(LoadTemp(state, divInt.Left), LoadTemp(state, divInt.Right), $"div_{divInt.Target}")),
            IrInst.DivFloat divFloat => StoreTemp(state, divFloat.Target, builder.BuildFDiv(LoadTempAsFloat(state, divFloat.Left), LoadTempAsFloat(state, divFloat.Right), $"fdiv_{divFloat.Target}")),
            IrInst.CmpIntGe cmpIntGe => StoreTemp(state, cmpIntGe.Target, EmitIntComparison(state, LLVMIntPredicate.LLVMIntSGE, LoadTemp(state, cmpIntGe.Left), LoadTemp(state, cmpIntGe.Right), $"cmp_ge_{cmpIntGe.Target}")),
            IrInst.CmpFloatGe cmpFloatGe => StoreTemp(state, cmpFloatGe.Target, EmitFloatComparison(state, LLVMRealPredicate.LLVMRealOGE, LoadTempAsFloat(state, cmpFloatGe.Left), LoadTempAsFloat(state, cmpFloatGe.Right), $"fcmp_ge_{cmpFloatGe.Target}")),
            IrInst.CmpIntLe cmpIntLe => StoreTemp(state, cmpIntLe.Target, EmitIntComparison(state, LLVMIntPredicate.LLVMIntSLE, LoadTemp(state, cmpIntLe.Left), LoadTemp(state, cmpIntLe.Right), $"cmp_le_{cmpIntLe.Target}")),
            IrInst.CmpFloatLe cmpFloatLe => StoreTemp(state, cmpFloatLe.Target, EmitFloatComparison(state, LLVMRealPredicate.LLVMRealOLE, LoadTempAsFloat(state, cmpFloatLe.Left), LoadTempAsFloat(state, cmpFloatLe.Right), $"fcmp_le_{cmpFloatLe.Target}")),
            IrInst.CmpIntEq cmpIntEq => StoreTemp(state, cmpIntEq.Target, EmitIntComparison(state, LLVMIntPredicate.LLVMIntEQ, LoadTemp(state, cmpIntEq.Left), LoadTemp(state, cmpIntEq.Right), $"cmp_eq_{cmpIntEq.Target}")),
            IrInst.CmpFloatEq cmpFloatEq => StoreTemp(state, cmpFloatEq.Target, EmitFloatComparison(state, LLVMRealPredicate.LLVMRealOEQ, LoadTempAsFloat(state, cmpFloatEq.Left), LoadTempAsFloat(state, cmpFloatEq.Right), $"fcmp_eq_{cmpFloatEq.Target}")),
            IrInst.CmpIntNe cmpIntNe => StoreTemp(state, cmpIntNe.Target, EmitIntComparison(state, LLVMIntPredicate.LLVMIntNE, LoadTemp(state, cmpIntNe.Left), LoadTemp(state, cmpIntNe.Right), $"cmp_ne_{cmpIntNe.Target}")),
            IrInst.CmpFloatNe cmpFloatNe => StoreTemp(state, cmpFloatNe.Target, EmitFloatComparison(state, LLVMRealPredicate.LLVMRealONE, LoadTempAsFloat(state, cmpFloatNe.Left), LoadTempAsFloat(state, cmpFloatNe.Right), $"fcmp_ne_{cmpFloatNe.Target}")),
            IrInst.CmpStrEq cmpStrEq => StoreTemp(state, cmpStrEq.Target, EmitStringComparison(state, LoadTemp(state, cmpStrEq.Left), LoadTemp(state, cmpStrEq.Right))),
            IrInst.CmpStrNe cmpStrNe => StoreTemp(state, cmpStrNe.Target, EmitInvertBool(state, EmitStringComparison(state, LoadTemp(state, cmpStrNe.Left), LoadTemp(state, cmpStrNe.Right)), $"cmp_str_ne_{cmpStrNe.Target}")),
            IrInst.PrintInt printInt => state.Flavor == LlvmCodegenFlavor.Linux ? EmitPrintInt(state, LoadTemp(state, printInt.Source)) : ThrowWindowsInstructionNotSupported(printInt),
            IrInst.PrintStr printStr => state.Flavor == LlvmCodegenFlavor.Linux ? EmitPrintStringFromTemp(state, LoadTemp(state, printStr.Source), appendNewline: true) : ThrowWindowsInstructionNotSupported(printStr),
            IrInst.WriteStr writeStr => state.Flavor == LlvmCodegenFlavor.Linux ? EmitPrintStringFromTemp(state, LoadTemp(state, writeStr.Source), appendNewline: false) : ThrowWindowsInstructionNotSupported(writeStr),
            IrInst.PrintBool printBool => state.Flavor == LlvmCodegenFlavor.Linux ? EmitPrintBool(state, LoadTemp(state, printBool.Source)) : ThrowWindowsInstructionNotSupported(printBool),
            IrInst.ConcatStr concatStr => StoreTemp(state, concatStr.Target, EmitStringConcat(state, LoadTemp(state, concatStr.Left), LoadTemp(state, concatStr.Right))),
            IrInst.MakeClosure makeClosure => StoreTemp(state, makeClosure.Target, EmitMakeClosure(state, makeClosure.FuncLabel, LoadTemp(state, makeClosure.EnvPtrTemp))),
            IrInst.CallClosure callClosure => StoreTemp(state, callClosure.Target, EmitCallClosure(state, LoadTemp(state, callClosure.ClosureTemp), LoadTemp(state, callClosure.ArgTemp))),
            IrInst.LoadMemOffset loadMemOffset => StoreTemp(state, loadMemOffset.Target, LoadMemory(state, LoadTemp(state, loadMemOffset.BasePtr), loadMemOffset.OffsetBytes, $"load_mem_{loadMemOffset.Target}")),
            IrInst.StoreMemOffset storeMemOffset => StoreMemory(state, LoadTemp(state, storeMemOffset.BasePtr), storeMemOffset.OffsetBytes, LoadTemp(state, storeMemOffset.Source), $"store_mem_{storeMemOffset.OffsetBytes}"),
            IrInst.AllocAdt allocAdt => StoreTemp(state, allocAdt.Target, EmitAllocAdt(state, allocAdt.Tag, allocAdt.FieldCount)),
            IrInst.SetAdtField setAdtField => StoreMemory(state, LoadTemp(state, setAdtField.Ptr), 8 + (setAdtField.FieldIndex * 8), LoadTemp(state, setAdtField.Source), $"set_adt_field_{setAdtField.FieldIndex}"),
            IrInst.GetAdtTag getAdtTag => StoreTemp(state, getAdtTag.Target, LoadMemory(state, LoadTemp(state, getAdtTag.Ptr), 0, $"get_adt_tag_{getAdtTag.Target}")),
            IrInst.GetAdtField getAdtField => StoreTemp(state, getAdtField.Target, LoadMemory(state, LoadTemp(state, getAdtField.Ptr), 8 + (getAdtField.FieldIndex * 8), $"get_adt_field_{getAdtField.Target}")),
            IrInst.Jump jump => EmitJump(state, jump.Target),
            IrInst.JumpIfFalse jumpIfFalse => EmitJumpIfFalse(state, LoadTemp(state, jumpIfFalse.CondTemp), jumpIfFalse.Target, index),
            IrInst.Return ret => EmitReturn(state, ret.Source),
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

    private static LLVMValueRef LoadTempAsFloat(LlvmCodegenState state, int temp)
    {
        return state.Target.Builder.BuildBitCast(LoadTemp(state, temp), state.F64, $"tmpf_{temp}");
    }

    private static LLVMValueRef NormalizeToI64(LlvmCodegenState state, LLVMValueRef value)
    {
        return value.TypeOf.Kind switch
        {
            LLVMTypeKind.LLVMIntegerTypeKind when value.TypeOf.IntWidth == 64 => value,
            LLVMTypeKind.LLVMIntegerTypeKind => state.Target.Builder.BuildZExt(value, state.I64, "zext_i64"),
            LLVMTypeKind.LLVMDoubleTypeKind => state.Target.Builder.BuildBitCast(value, state.I64, "f64_i64"),
            LLVMTypeKind.LLVMPointerTypeKind => state.Target.Builder.BuildPtrToInt(value, state.I64, "ptr_i64"),
            _ => throw new InvalidOperationException($"Cannot normalize LLVM value of type '{value.TypeOf.Kind}' to i64.")
        };
    }

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

    private static LLVMValueRef EmitAlloc(LlvmCodegenState state, int sizeBytes)
    {
        LLVMValueRef cursor = state.Target.Builder.BuildLoad2(state.I64, state.HeapCursorSlot, "heap_cursor_value");
        LLVMValueRef nextCursor = state.Target.Builder.BuildAdd(cursor, LLVMValueRef.CreateConstInt(state.I64, (ulong)sizeBytes, false), "heap_cursor_next");
        state.Target.Builder.BuildStore(nextCursor, state.HeapCursorSlot);
        return cursor;
    }

    private static LLVMValueRef EmitAllocAdt(LlvmCodegenState state, int tag, int fieldCount)
    {
        LLVMValueRef ptr = EmitAlloc(state, (1 + fieldCount) * 8);
        StoreMemory(state, ptr, 0, LLVMValueRef.CreateConstInt(state.I64, (ulong)tag, false), $"adt_tag_{tag}");
        return ptr;
    }

    private static bool StoreMemory(LlvmCodegenState state, LLVMValueRef baseAddress, int offsetBytes, LLVMValueRef value, string name)
    {
        LLVMValueRef ptr = GetMemoryPointer(state, baseAddress, offsetBytes, name + "_ptr");
        state.Target.Builder.BuildStore(NormalizeToI64(state, value), ptr);
        return false;
    }

    private static LLVMValueRef LoadMemory(LlvmCodegenState state, LLVMValueRef baseAddress, int offsetBytes, string name)
    {
        LLVMValueRef ptr = GetMemoryPointer(state, baseAddress, offsetBytes, name + "_ptr");
        return state.Target.Builder.BuildLoad2(state.I64, ptr, name);
    }

    private static LLVMValueRef GetMemoryPointer(LlvmCodegenState state, LLVMValueRef baseAddress, int offsetBytes, string name)
    {
        LLVMValueRef basePtr = state.Target.Builder.BuildIntToPtr(baseAddress, state.I8Ptr, name + "_base");
        LLVMValueRef bytePtr = state.Target.Builder.BuildGEP2(
            state.I8,
            basePtr,
            new[]
            {
                LLVMValueRef.CreateConstInt(state.I64, (ulong)offsetBytes, false)
            },
            name + "_byte");
        return state.Target.Builder.BuildBitCast(bytePtr, state.I64Ptr, name);
    }

    private static LLVMValueRef EmitStringComparison(LlvmCodegenState state, LLVMValueRef leftRef, LLVMValueRef rightRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "str_cmp_result");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, "str_cmp_idx");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);

        LLVMValueRef leftLen = LoadStringLength(state, leftRef, "str_cmp_left_len");
        LLVMValueRef rightLen = LoadStringLength(state, rightRef, "str_cmp_right_len");
        LLVMValueRef leftBytes = GetStringBytesPointer(state, leftRef, "str_cmp_left_bytes");
        LLVMValueRef rightBytes = GetStringBytesPointer(state, rightRef, "str_cmp_right_bytes");

        var lenEqBlock = state.Function.AppendBasicBlock("str_cmp_len_eq");
        var notEqBlock = state.Function.AppendBasicBlock("str_cmp_not_eq");
        var loopCheckBlock = state.Function.AppendBasicBlock("str_cmp_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock("str_cmp_loop_body");
        var eqBlock = state.Function.AppendBasicBlock("str_cmp_eq");
        var continueBlock = state.Function.AppendBasicBlock("str_cmp_continue");

        LLVMValueRef lenEq = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, leftLen, rightLen, "str_cmp_len_match");
        builder.BuildCondBr(lenEq, lenEqBlock, notEqBlock);

        builder.PositionAtEnd(notEqBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(lenEqBlock);
        LLVMValueRef isEmpty = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, leftLen, LLVMValueRef.CreateConstInt(state.I64, 0, false), "str_cmp_empty");
        builder.BuildCondBr(isEmpty, eqBlock, loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, "str_cmp_index");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, index, leftLen, "str_cmp_done");
        builder.BuildCondBr(done, eqBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef leftBytePtr = builder.BuildGEP2(state.I8, leftBytes, new[] { index }, "str_cmp_left_byte_ptr");
        LLVMValueRef rightBytePtr = builder.BuildGEP2(state.I8, rightBytes, new[] { index }, "str_cmp_right_byte_ptr");
        LLVMValueRef leftByte = builder.BuildLoad2(state.I8, leftBytePtr, "str_cmp_left_byte");
        LLVMValueRef rightByte = builder.BuildLoad2(state.I8, rightBytePtr, "str_cmp_right_byte");
        LLVMValueRef bytesEq = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, leftByte, rightByte, "str_cmp_bytes_eq");
        LLVMValueRef nextIndex = builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), "str_cmp_next_index");
        builder.BuildStore(nextIndex, indexSlot);
        builder.BuildCondBr(bytesEq, loopCheckBlock, notEqBlock);

        builder.PositionAtEnd(eqBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 1, false), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "str_cmp_result_value");
    }

    private static LLVMValueRef EmitStringConcat(LlvmCodegenState state, LLVMValueRef leftRef, LLVMValueRef rightRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef leftLen = LoadStringLength(state, leftRef, "str_cat_left_len");
        LLVMValueRef rightLen = LoadStringLength(state, rightRef, "str_cat_right_len");
        LLVMValueRef totalLen = builder.BuildAdd(leftLen, rightLen, "str_cat_total_len");
        LLVMValueRef totalBytes = builder.BuildAdd(totalLen, LLVMValueRef.CreateConstInt(state.I64, 8, false), "str_cat_total_bytes");
        LLVMValueRef destRef = EmitAllocDynamic(state, totalBytes);
        StoreMemory(state, destRef, 0, totalLen, "str_cat_len");

        LLVMValueRef destBytes = GetStringBytesPointer(state, destRef, "str_cat_dest_bytes");
        LLVMValueRef leftBytes = GetStringBytesPointer(state, leftRef, "str_cat_left_bytes");
        LLVMValueRef rightBytes = GetStringBytesPointer(state, rightRef, "str_cat_right_bytes");
        EmitCopyBytes(state, destBytes, leftBytes, leftLen, "str_cat_copy_left");
        LLVMValueRef rightDest = builder.BuildGEP2(state.I8, destBytes, new[] { leftLen }, "str_cat_right_dest");
        EmitCopyBytes(state, rightDest, rightBytes, rightLen, "str_cat_copy_right");
        return destRef;
    }

    private static void EmitCopyBytes(LlvmCodegenState state, LLVMValueRef destBytes, LLVMValueRef sourceBytes, LLVMValueRef length, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_idx");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);

        var checkBlock = state.Function.AppendBasicBlock(prefix + "_check");
        var bodyBlock = state.Function.AppendBasicBlock(prefix + "_body");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");
        builder.BuildBr(checkBlock);

        builder.PositionAtEnd(checkBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, index, length, prefix + "_done");
        builder.BuildCondBr(done, continueBlock, bodyBlock);

        builder.PositionAtEnd(bodyBlock);
        LLVMValueRef sourcePtr = builder.BuildGEP2(state.I8, sourceBytes, new[] { index }, prefix + "_src_ptr");
        LLVMValueRef destPtr = builder.BuildGEP2(state.I8, destBytes, new[] { index }, prefix + "_dst_ptr");
        LLVMValueRef value = builder.BuildLoad2(state.I8, sourcePtr, prefix + "_value");
        builder.BuildStore(value, destPtr);
        LLVMValueRef nextIndex = builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_next");
        builder.BuildStore(nextIndex, indexSlot);
        builder.BuildBr(checkBlock);

        builder.PositionAtEnd(continueBlock);
    }

    private static LLVMValueRef LoadStringLength(LlvmCodegenState state, LLVMValueRef stringRef, string name)
    {
        return LoadMemory(state, stringRef, 0, name);
    }

    private static LLVMValueRef GetStringBytesPointer(LlvmCodegenState state, LLVMValueRef stringRef, string name)
    {
        LLVMValueRef byteAddress = state.Target.Builder.BuildAdd(stringRef, LLVMValueRef.CreateConstInt(state.I64, 8, false), name + "_addr");
        return state.Target.Builder.BuildIntToPtr(byteAddress, state.I8Ptr, name);
    }

    private static LLVMValueRef EmitAllocDynamic(LlvmCodegenState state, LLVMValueRef sizeBytes)
    {
        LLVMValueRef cursor = state.Target.Builder.BuildLoad2(state.I64, state.HeapCursorSlot, "heap_cursor_value_dyn");
        LLVMValueRef nextCursor = state.Target.Builder.BuildAdd(cursor, NormalizeToI64(state, sizeBytes), "heap_cursor_next_dyn");
        state.Target.Builder.BuildStore(nextCursor, state.HeapCursorSlot);
        return cursor;
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

    private static void EmitEntryProgramArgsInitialization(LlvmCodegenState state)
    {
        state.Target.Builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.ProgramArgsSlot);

        if (state.Flavor == LlvmCodegenFlavor.Linux)
        {
            EmitLinuxProgramArgsInitialization(state);
            return;
        }

        throw new InvalidOperationException("The minimal Windows LLVM path does not yet support program args initialization.");
    }

    private static void EmitLinuxProgramArgsInitialization(LlvmCodegenState state)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef listSlot = builder.BuildAlloca(state.I64, "program_args_list");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, "program_args_index");
        LLVMValueRef argPtrSlot = builder.BuildAlloca(state.I64, "program_args_arg_ptr");
        LLVMValueRef lenSlot = builder.BuildAlloca(state.I64, "program_args_arg_len");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), listSlot);

        LLVMValueRef stackPtr = state.EntryStackPointer;
        LLVMValueRef argc = LoadMemory(state, stackPtr, 0, "program_args_argc");

        var initBlock = state.Function.AppendBasicBlock("program_args_init");
        var loopCheckBlock = state.Function.AppendBasicBlock("program_args_loop_check");
        var lenCheckBlock = state.Function.AppendBasicBlock("program_args_len_check");
        var lenBodyBlock = state.Function.AppendBasicBlock("program_args_len_body");
        var buildNodeBlock = state.Function.AppendBasicBlock("program_args_build_node");
        var doneBlock = state.Function.AppendBasicBlock("program_args_done");

        LLVMValueRef hasArgs = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntSGT,
            argc,
            LLVMValueRef.CreateConstInt(state.I64, 1, false),
            "program_args_has_args");
        builder.BuildCondBr(hasArgs, initBlock, doneBlock);

        builder.PositionAtEnd(initBlock);
        builder.BuildStore(
            builder.BuildSub(argc, LLVMValueRef.CreateConstInt(state.I64, 1, false), "program_args_start_index"),
            indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, "program_args_index_value");
        LLVMValueRef shouldContinue = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntSGT,
            index,
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "program_args_continue");
        builder.BuildCondBr(shouldContinue, lenCheckBlock, doneBlock);

        builder.PositionAtEnd(lenCheckBlock);
        LLVMValueRef argvEntryOffset = builder.BuildMul(index, LLVMValueRef.CreateConstInt(state.I64, 8, false), "program_args_argv_entry_offset");
        LLVMValueRef argvEntryAddress = builder.BuildAdd(
            stackPtr,
            builder.BuildAdd(LLVMValueRef.CreateConstInt(state.I64, 8, false), argvEntryOffset, "program_args_argv_offset"),
            "program_args_argv_entry_addr");
        LLVMValueRef argPtr = LoadMemory(state, argvEntryAddress, 0, "program_args_argv_entry");
        builder.BuildStore(argPtr, argPtrSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), lenSlot);

        var lenLoopCheckBlock = state.Function.AppendBasicBlock("program_args_len_loop_check");
        builder.BuildBr(lenLoopCheckBlock);

        builder.PositionAtEnd(lenLoopCheckBlock);
        LLVMValueRef currentLen = builder.BuildLoad2(state.I64, lenSlot, "program_args_current_len");
        LLVMValueRef currentArgPtr = builder.BuildLoad2(state.I64, argPtrSlot, "program_args_current_arg_ptr");
        LLVMValueRef currentBytePtr = builder.BuildGEP2(
            state.I8,
            builder.BuildIntToPtr(currentArgPtr, state.I8Ptr, "program_args_arg_bytes"),
            new[] { currentLen },
            "program_args_current_byte_ptr");
        LLVMValueRef currentByte = builder.BuildLoad2(state.I8, currentBytePtr, "program_args_current_byte");
        LLVMValueRef reachedTerminator = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntEQ,
            currentByte,
            LLVMValueRef.CreateConstInt(state.I8, 0, false),
            "program_args_reached_terminator");
        builder.BuildCondBr(reachedTerminator, buildNodeBlock, lenBodyBlock);

        builder.PositionAtEnd(lenBodyBlock);
        builder.BuildStore(
            builder.BuildAdd(currentLen, LLVMValueRef.CreateConstInt(state.I64, 1, false), "program_args_next_len"),
            lenSlot);
        builder.BuildBr(lenLoopCheckBlock);

        builder.PositionAtEnd(buildNodeBlock);
        LLVMValueRef argLen = builder.BuildLoad2(state.I64, lenSlot, "program_args_arg_len_value");
        LLVMValueRef stringRef = EmitAllocDynamic(
            state,
            builder.BuildAdd(argLen, LLVMValueRef.CreateConstInt(state.I64, 8, false), "program_args_string_bytes"));
        StoreMemory(state, stringRef, 0, argLen, "program_args_string_len");
        EmitCopyBytes(
            state,
            GetStringBytesPointer(state, stringRef, "program_args_string_dest"),
            builder.BuildIntToPtr(builder.BuildLoad2(state.I64, argPtrSlot, "program_args_copy_arg_ptr"), state.I8Ptr, "program_args_string_src"),
            argLen,
            "program_args_copy_bytes");
        LLVMValueRef consRef = EmitAlloc(state, 16);
        StoreMemory(state, consRef, 0, stringRef, "program_args_cons_head");
        StoreMemory(state, consRef, 8, builder.BuildLoad2(state.I64, listSlot, "program_args_prev_list"), "program_args_cons_tail");
        builder.BuildStore(consRef, listSlot);
        builder.BuildStore(
            builder.BuildSub(builder.BuildLoad2(state.I64, indexSlot, "program_args_index_before_dec"), LLVMValueRef.CreateConstInt(state.I64, 1, false), "program_args_index_dec"),
            indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(doneBlock);
        builder.BuildStore(builder.BuildLoad2(state.I64, listSlot, "program_args_final_list"), state.ProgramArgsSlot);
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
        IReadOnlyDictionary<string, LLVMValueRef> LiftedFunctions,
        LLVMValueRef ProgramArgsSlot,
        LLVMValueRef[] TempSlots,
        LLVMValueRef[] LocalSlots,
        LLVMValueRef HeapCursorSlot,
        Dictionary<string, LLVMBasicBlockRef> LabelBlocks,
        Dictionary<int, LLVMBasicBlockRef> FallthroughBlocks,
        LLVMTypeRef I64,
        LLVMTypeRef I8,
        LLVMTypeRef F64,
        LLVMTypeRef I8Ptr,
        LLVMTypeRef I64Ptr,
        LLVMValueRef EntryStackPointer,
        LlvmCodegenFlavor Flavor,
        bool UsesProgramArgs,
        bool IsEntry)
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
        return SupportsMinimalLlvm(program, static instruction => instruction switch
        {
            IrInst.LoadConstInt => true,
            IrInst.LoadConstFloat => true,
            IrInst.LoadConstBool => true,
            IrInst.LoadConstStr => true,
            IrInst.LoadProgramArgs => true,
            IrInst.LoadLocal => true,
            IrInst.StoreLocal => true,
            IrInst.LoadEnv => true,
            IrInst.Alloc => true,
            IrInst.AddInt => true,
            IrInst.AddFloat => true,
            IrInst.SubInt => true,
            IrInst.SubFloat => true,
            IrInst.MulInt => true,
            IrInst.MulFloat => true,
            IrInst.DivInt => true,
            IrInst.DivFloat => true,
            IrInst.CmpIntGe => true,
            IrInst.CmpFloatGe => true,
            IrInst.CmpIntLe => true,
            IrInst.CmpFloatLe => true,
            IrInst.CmpIntEq => true,
            IrInst.CmpFloatEq => true,
            IrInst.CmpIntNe => true,
            IrInst.CmpFloatNe => true,
            IrInst.CmpStrEq => true,
            IrInst.CmpStrNe => true,
            IrInst.LoadMemOffset => true,
            IrInst.StoreMemOffset => true,
            IrInst.ConcatStr => true,
            IrInst.MakeClosure => true,
            IrInst.CallClosure => true,
            IrInst.AllocAdt => true,
            IrInst.SetAdtField => true,
            IrInst.GetAdtTag => true,
            IrInst.GetAdtField => true,
            IrInst.Jump => true,
            IrInst.JumpIfFalse => true,
            IrInst.Return => true,
            IrInst.Label => true,
            _ => false
        });
    }

    private static bool SupportsMinimalLinuxLlvm(IrProgram program)
    {
        return SupportsMinimalLlvm(program, static instruction => instruction switch
        {
            IrInst.LoadConstInt => true,
            IrInst.LoadConstFloat => true,
            IrInst.LoadConstBool => true,
            IrInst.LoadConstStr => true,
            IrInst.LoadProgramArgs => true,
            IrInst.LoadLocal => true,
            IrInst.StoreLocal => true,
            IrInst.LoadEnv => true,
            IrInst.Alloc => true,
            IrInst.AddInt => true,
            IrInst.AddFloat => true,
            IrInst.SubInt => true,
            IrInst.SubFloat => true,
            IrInst.MulInt => true,
            IrInst.MulFloat => true,
            IrInst.DivInt => true,
            IrInst.DivFloat => true,
            IrInst.CmpIntGe => true,
            IrInst.CmpFloatGe => true,
            IrInst.CmpIntLe => true,
            IrInst.CmpFloatLe => true,
            IrInst.CmpIntEq => true,
            IrInst.CmpFloatEq => true,
            IrInst.CmpIntNe => true,
            IrInst.CmpFloatNe => true,
            IrInst.CmpStrEq => true,
            IrInst.CmpStrNe => true,
            IrInst.PrintInt => true,
            IrInst.PrintStr => true,
            IrInst.WriteStr => true,
            IrInst.PrintBool => true,
            IrInst.LoadMemOffset => true,
            IrInst.StoreMemOffset => true,
            IrInst.ConcatStr => true,
            IrInst.MakeClosure => true,
            IrInst.CallClosure => true,
            IrInst.AllocAdt => true,
            IrInst.SetAdtField => true,
            IrInst.GetAdtTag => true,
            IrInst.GetAdtField => true,
            IrInst.Jump => true,
            IrInst.JumpIfFalse => true,
            IrInst.Return => true,
            IrInst.Label => true,
            _ => false
        });
    }

    private static bool SupportsMinimalLlvm(IrProgram program, Func<IrInst, bool> isSupportedInstruction)
    {
        foreach (IrInst instruction in program.EntryFunction.Instructions)
        {
            if (!isSupportedInstruction(instruction))
            {
                return false;
            }
        }

        foreach (IrFunction function in program.Functions)
        {
            if (function.Instructions.Any(static instruction => instruction is IrInst.LoadProgramArgs))
            {
                return false;
            }

            if (function.Instructions.Any(RequiresEntryHeapStorage))
            {
                return false;
            }

            foreach (IrInst instruction in function.Instructions)
            {
                if (!isSupportedInstruction(instruction))
                {
                    return false;
                }
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
