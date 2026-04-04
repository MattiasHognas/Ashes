using System.Buffers.Binary;
using Ashes.Backend.Backends;
using Ashes.Semantics;
using LLVMSharp.Interop;

namespace Ashes.Backend.Llvm;

internal static class LlvmCodegen
{
    private const int HeapSizeBytes = 1024 * 1024 * 4;
    private const int InputBufSize = 64 * 1024;
    private const int MaxFileReadBytes = 1024 * 1024;
    private const uint Utf8CodePage = 65001;
    private const uint StdOutputHandle = 0xFFFFFFF5;
    private const uint StdInputHandle = 0xFFFFFFF6;
    private const long SyscallRead = 0;
    private const long SyscallWrite = 1;
    private const long SyscallOpen = 2;
    private const long SyscallClose = 3;
    private const long SyscallLseek = 8;
    private const long SyscallSocket = 41;
    private const long SyscallConnect = 42;
    private const long SyscallExit = 60;
    private const string FileReadFailedMessage = "Ashes.File.readText() failed";
    private const string FileWriteFailedMessage = "Ashes.File.writeText() failed";
    private const string FileReadInvalidUtf8Message = "Ashes.File.readText() encountered invalid UTF-8";
    private const string TcpConnectFailedMessage = "Ashes.Net.Tcp.connect() failed";
    private const string TcpSendFailedMessage = "Ashes.Net.Tcp.send() failed";
    private const string TcpReceiveFailedMessage = "Ashes.Net.Tcp.receive() failed";
    private const string TcpCloseFailedMessage = "Ashes.Net.Tcp.close() failed";
    private const string TcpInvalidUtf8Message = "Ashes.Net.Tcp.receive() encountered invalid UTF-8";
    private const string TcpInvalidMaxBytesMessage = "Ashes.Net.Tcp.receive() maxBytes must be positive";
    private const string TcpResolveFailedMessage = "Ashes.Net.Tcp.connect() could not resolve host";
    private const string HttpHttpsNotSupportedMessage = "https not supported";
    private const string HttpMalformedUrlMessage = "malformed URL";
    private const string HttpMalformedResponseMessage = "malformed HTTP response";
    private const string HttpUnsupportedTransferEncodingMessage = "unsupported transfer encoding";

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
        LLVMTypeRef i32 = target.Context.Int32Type;
        LLVMTypeRef i8 = target.Context.Int8Type;
        LLVMTypeRef f64 = target.Context.DoubleType;
        LLVMTypeRef voidType = target.Context.VoidType;
        LLVMTypeRef i8Ptr = LLVMTypeRef.CreatePointer(i8, 0);
        LLVMTypeRef i32Ptr = LLVMTypeRef.CreatePointer(i32, 0);
        LLVMTypeRef i64Ptr = LLVMTypeRef.CreatePointer(i64, 0);
        LLVMTypeRef heapType = LLVMTypeRef.CreateArray(i8, HeapSizeBytes);
        var stringLiterals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        LLVMTypeRef closureFunctionType = LLVMTypeRef.CreateFunction(i64, [i64, i64]);
        bool usesProgramArgs = ProgramUsesInstruction<IrInst.LoadProgramArgs>(program);
        bool usesReadLine = ProgramUsesInstruction<IrInst.ReadLine>(program);
        bool usesWindowsStdout = flavor == LlvmCodegenFlavor.Windows
            && (ProgramUsesInstruction<IrInst.PrintInt>(program)
                || ProgramUsesInstruction<IrInst.PrintStr>(program)
                || ProgramUsesInstruction<IrInst.WriteStr>(program)
                || ProgramUsesInstruction<IrInst.PrintBool>(program)
                || ProgramUsesInstruction<IrInst.PanicStr>(program)
                || usesReadLine);
        bool usesWindowsExitProcess = flavor == LlvmCodegenFlavor.Windows
            && (ProgramUsesInstruction<IrInst.PanicStr>(program)
                || usesReadLine);
        bool usesWindowsProgramArgs = flavor == LlvmCodegenFlavor.Windows
            && usesProgramArgs;
        bool usesWindowsReadLine = flavor == LlvmCodegenFlavor.Windows
            && usesReadLine;
        bool usesWindowsFileOps = flavor == LlvmCodegenFlavor.Windows
            && (ProgramUsesInstruction<IrInst.FileReadText>(program)
                || ProgramUsesInstruction<IrInst.FileWriteText>(program)
                || ProgramUsesInstruction<IrInst.FileExists>(program));
        bool usesWindowsSockets = flavor == LlvmCodegenFlavor.Windows
            && (ProgramUsesInstruction<IrInst.HttpGet>(program)
                || ProgramUsesInstruction<IrInst.HttpPost>(program)
                || ProgramUsesInstruction<IrInst.NetTcpConnect>(program)
                || ProgramUsesInstruction<IrInst.NetTcpSend>(program)
                || ProgramUsesInstruction<IrInst.NetTcpReceive>(program)
                || ProgramUsesInstruction<IrInst.NetTcpClose>(program));
        LLVMValueRef windowsGetStdHandleImport = default;
        LLVMValueRef windowsWriteFileImport = default;
        LLVMValueRef windowsReadFileImport = default;
        LLVMValueRef windowsCreateFileImport = default;
        LLVMValueRef windowsCloseHandleImport = default;
        LLVMValueRef windowsGetFileAttributesImport = default;
        LLVMValueRef windowsWsaStartupImport = default;
        LLVMValueRef windowsSocketImport = default;
        LLVMValueRef windowsConnectImport = default;
        LLVMValueRef windowsSendImport = default;
        LLVMValueRef windowsRecvImport = default;
        LLVMValueRef windowsCloseSocketImport = default;
        LLVMValueRef windowsExitProcessImport = default;
        LLVMValueRef windowsGetCommandLineImport = default;
        LLVMValueRef windowsWideCharToMultiByteImport = default;
        LLVMValueRef windowsLocalFreeImport = default;
        LLVMValueRef windowsCommandLineToArgvImport = default;
        LLVMValueRef heapStorageGlobal = target.Module.AddGlobal(heapType, "__ashes_heap_storage");
        heapStorageGlobal.Linkage = LLVMLinkage.LLVMInternalLinkage;
        heapStorageGlobal.Initializer = LLVMValueRef.CreateConstNull(heapType);
        LLVMValueRef heapCursorGlobal = target.Module.AddGlobal(i64, "__ashes_heap_cursor");
        heapCursorGlobal.Linkage = LLVMLinkage.LLVMInternalLinkage;
        heapCursorGlobal.Initializer = LLVMValueRef.CreateConstInt(i64, 0, false);
        if (usesWindowsStdout || usesWindowsReadLine)
        {
            LLVMTypeRef getStdHandleType = LLVMTypeRef.CreateFunction(i64, [i32]);
            windowsGetStdHandleImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(getStdHandleType, 0), "__imp_GetStdHandle");
            windowsGetStdHandleImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
        }

        if (usesWindowsStdout)
        {
            LLVMTypeRef writeFileType = LLVMTypeRef.CreateFunction(i32, [i64, i8Ptr, i32, i32Ptr, i8Ptr]);
            windowsWriteFileImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(writeFileType, 0), "__imp_WriteFile");
            windowsWriteFileImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
        }

        if (usesWindowsReadLine)
        {
            LLVMTypeRef readFileType = LLVMTypeRef.CreateFunction(i32, [i64, i8Ptr, i32, i32Ptr, i8Ptr]);
            windowsReadFileImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(readFileType, 0), "__imp_ReadFile");
            windowsReadFileImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
        }

        if (usesWindowsFileOps)
        {
            LLVMTypeRef createFileType = LLVMTypeRef.CreateFunction(i64, [i8Ptr, i32, i32, i8Ptr, i32, i32, i64]);
            LLVMTypeRef closeHandleType = LLVMTypeRef.CreateFunction(i32, [i64]);
            LLVMTypeRef getFileAttributesType = LLVMTypeRef.CreateFunction(i32, [i8Ptr]);
            windowsCreateFileImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(createFileType, 0), "__imp_CreateFileA");
            windowsCreateFileImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
            windowsCloseHandleImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(closeHandleType, 0), "__imp_CloseHandle");
            windowsCloseHandleImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
            windowsGetFileAttributesImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(getFileAttributesType, 0), "__imp_GetFileAttributesA");
            windowsGetFileAttributesImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
        }

        if (usesWindowsSockets)
        {
            LLVMTypeRef wsaStartupType = LLVMTypeRef.CreateFunction(i32, [target.Context.Int16Type, i8Ptr]);
            LLVMTypeRef socketType = LLVMTypeRef.CreateFunction(i64, [i32, i32, i32]);
            LLVMTypeRef connectType = LLVMTypeRef.CreateFunction(i32, [i64, i8Ptr, i32]);
            LLVMTypeRef sendType = LLVMTypeRef.CreateFunction(i32, [i64, i8Ptr, i32, i32]);
            LLVMTypeRef recvType = LLVMTypeRef.CreateFunction(i32, [i64, i8Ptr, i32, i32]);
            LLVMTypeRef closeSocketType = LLVMTypeRef.CreateFunction(i32, [i64]);
            windowsWsaStartupImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(wsaStartupType, 0), "__imp_WSAStartup");
            windowsWsaStartupImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
            windowsSocketImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(socketType, 0), "__imp_socket");
            windowsSocketImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
            windowsConnectImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(connectType, 0), "__imp_connect");
            windowsConnectImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
            windowsSendImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(sendType, 0), "__imp_send");
            windowsSendImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
            windowsRecvImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(recvType, 0), "__imp_recv");
            windowsRecvImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
            windowsCloseSocketImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(closeSocketType, 0), "__imp_closesocket");
            windowsCloseSocketImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
        }

        if (usesWindowsExitProcess)
        {
            LLVMTypeRef exitProcessType = LLVMTypeRef.CreateFunction(voidType, [i32]);
            windowsExitProcessImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(exitProcessType, 0), "__imp_ExitProcess");
            windowsExitProcessImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
        }

        if (usesWindowsProgramArgs)
        {
            LLVMTypeRef i16 = target.Context.Int16Type;
            LLVMTypeRef i16Ptr = LLVMTypeRef.CreatePointer(i16, 0);
            LLVMTypeRef i16PtrPtr = LLVMTypeRef.CreatePointer(i16Ptr, 0);
            LLVMTypeRef getCommandLineType = LLVMTypeRef.CreateFunction(i16Ptr, []);
            LLVMTypeRef wideCharToMultiByteType = LLVMTypeRef.CreateFunction(i32, [i32, i32, i16Ptr, i32, i8Ptr, i32, i8Ptr, i8Ptr]);
            LLVMTypeRef localFreeType = LLVMTypeRef.CreateFunction(i8Ptr, [i8Ptr]);
            LLVMTypeRef commandLineToArgvType = LLVMTypeRef.CreateFunction(i16PtrPtr, [i16Ptr, i32Ptr]);

            windowsGetCommandLineImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(getCommandLineType, 0), "__imp_GetCommandLineW");
            windowsGetCommandLineImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
            windowsWideCharToMultiByteImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(wideCharToMultiByteType, 0), "__imp_WideCharToMultiByte");
            windowsWideCharToMultiByteImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
            windowsLocalFreeImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(localFreeType, 0), "__imp_LocalFree");
            windowsLocalFreeImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
            windowsCommandLineToArgvImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(commandLineToArgvType, 0), "__imp_CommandLineToArgvW");
            windowsCommandLineToArgvImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
        }

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
            i32,
            i32Ptr,
            heapStorageGlobal,
            heapCursorGlobal,
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
            windowsExitProcessImport,
            windowsGetCommandLineImport,
            windowsWideCharToMultiByteImport,
            windowsLocalFreeImport,
            windowsCommandLineToArgvImport,
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
                i32,
                i32Ptr,
                heapStorageGlobal,
                heapCursorGlobal,
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
                windowsExitProcessImport,
                windowsGetCommandLineImport,
                windowsWideCharToMultiByteImport,
                windowsLocalFreeImport,
                windowsCommandLineToArgvImport,
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
        LLVMTypeRef i32,
        LLVMTypeRef i32Ptr,
        LLVMValueRef heapStorageGlobal,
        LLVMValueRef heapCursorGlobal,
        LLVMValueRef windowsGetStdHandleImport,
        LLVMValueRef windowsWriteFileImport,
        LLVMValueRef windowsReadFileImport,
        LLVMValueRef windowsCreateFileImport,
        LLVMValueRef windowsCloseHandleImport,
        LLVMValueRef windowsGetFileAttributesImport,
        LLVMValueRef windowsWsaStartupImport,
        LLVMValueRef windowsSocketImport,
        LLVMValueRef windowsConnectImport,
        LLVMValueRef windowsSendImport,
        LLVMValueRef windowsRecvImport,
        LLVMValueRef windowsCloseSocketImport,
        LLVMValueRef windowsExitProcessImport,
        LLVMValueRef windowsGetCommandLineImport,
        LLVMValueRef windowsWideCharToMultiByteImport,
        LLVMValueRef windowsLocalFreeImport,
        LLVMValueRef windowsCommandLineToArgvImport,
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

        if (isEntry)
        {
            LLVMValueRef heapBasePtr = target.Builder.BuildGEP2(
                LLVMTypeRef.CreateArray(i8, HeapSizeBytes),
                heapStorageGlobal,
                new[]
                {
                    LLVMValueRef.CreateConstInt(i64, 0, false),
                    LLVMValueRef.CreateConstInt(i64, 0, false)
                },
                "heap_base_ptr");
            target.Builder.BuildStore(target.Builder.BuildPtrToInt(heapBasePtr, i64, "heap_base_i64"), heapCursorGlobal);
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
            heapCursorGlobal,
            labelBlocks,
            fallthroughBlocks,
            i64,
            i32,
            i8,
            f64,
            i8Ptr,
            i32Ptr,
            i64Ptr,
            entryStackPointer,
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
            windowsExitProcessImport,
            windowsGetCommandLineImport,
            windowsWideCharToMultiByteImport,
            windowsLocalFreeImport,
            windowsCommandLineToArgvImport,
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
            IrInst.ReadLine readLine => StoreTemp(state, readLine.Target, EmitReadLine(state)),
            IrInst.FileReadText fileReadText => StoreTemp(state, fileReadText.Target, EmitFileReadText(state, LoadTemp(state, fileReadText.PathTemp))),
            IrInst.FileWriteText fileWriteText => StoreTemp(state, fileWriteText.Target, EmitFileWriteText(state, LoadTemp(state, fileWriteText.PathTemp), LoadTemp(state, fileWriteText.TextTemp))),
            IrInst.FileExists fileExists => StoreTemp(state, fileExists.Target, EmitFileExists(state, LoadTemp(state, fileExists.PathTemp))),
            IrInst.HttpGet httpGet => StoreTemp(state, httpGet.Target, EmitHttpRequest(state, LoadTemp(state, httpGet.UrlTemp), LLVMValueRef.CreateConstInt(state.I64, 0, false), hasBody: false)),
            IrInst.HttpPost httpPost => StoreTemp(state, httpPost.Target, EmitHttpRequest(state, LoadTemp(state, httpPost.UrlTemp), LoadTemp(state, httpPost.BodyTemp), hasBody: true)),
            IrInst.NetTcpConnect tcpConnect => StoreTemp(state, tcpConnect.Target, EmitTcpConnect(state, LoadTemp(state, tcpConnect.HostTemp), LoadTemp(state, tcpConnect.PortTemp))),
            IrInst.NetTcpSend tcpSend => StoreTemp(state, tcpSend.Target, EmitTcpSend(state, LoadTemp(state, tcpSend.SocketTemp), LoadTemp(state, tcpSend.TextTemp))),
            IrInst.NetTcpReceive tcpReceive => StoreTemp(state, tcpReceive.Target, EmitTcpReceive(state, LoadTemp(state, tcpReceive.SocketTemp), LoadTemp(state, tcpReceive.MaxBytesTemp))),
            IrInst.NetTcpClose tcpClose => StoreTemp(state, tcpClose.Target, EmitTcpClose(state, LoadTemp(state, tcpClose.SocketTemp))),
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
            IrInst.PrintInt printInt => EmitPrintInt(state, LoadTemp(state, printInt.Source)),
            IrInst.PrintStr printStr => EmitPrintStringFromTemp(state, LoadTemp(state, printStr.Source), appendNewline: true),
            IrInst.WriteStr writeStr => EmitPrintStringFromTemp(state, LoadTemp(state, writeStr.Source), appendNewline: false),
            IrInst.PrintBool printBool => EmitPrintBool(state, LoadTemp(state, printBool.Source)),
            IrInst.PanicStr panicStr => EmitPanic(state, LoadTemp(state, panicStr.Source)),
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

    private static LLVMValueRef EmitReadLine(LlvmCodegenState state)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef inputBufType = LLVMTypeRef.CreateArray(state.I8, InputBufSize);
        LLVMValueRef inputBuf = builder.BuildAlloca(inputBufType, "read_line_buf");
        LLVMValueRef inputBufPtr = GetArrayElementPointer(state, inputBufType, inputBuf, LLVMValueRef.CreateConstInt(state.I64, 0, false), "read_line_buf_ptr");
        LLVMValueRef byteSlot = builder.BuildAlloca(state.I8, "read_line_byte");
        LLVMValueRef lenSlot = builder.BuildAlloca(state.I64, "read_line_len");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "read_line_result");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), lenSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);

        LLVMValueRef stdinHandle = default;
        LLVMValueRef bytesReadSlot = default;
        if (state.Flavor == LlvmCodegenFlavor.Windows)
        {
            stdinHandle = EmitWindowsGetStdHandle(state, StdInputHandle, "stdin_handle");
            bytesReadSlot = builder.BuildAlloca(state.I32, "read_line_bytes_read");
        }

        var loopBlock = state.Function.AppendBasicBlock("read_line_loop");
        var inspectBlock = state.Function.AppendBasicBlock("read_line_inspect");
        var skipCrBlock = state.Function.AppendBasicBlock("read_line_skip_cr");
        var storeByteBlock = state.Function.AppendBasicBlock("read_line_store_byte");
        var appendByteBlock = state.Function.AppendBasicBlock("read_line_append_byte");
        var eofBlock = state.Function.AppendBasicBlock("read_line_eof");
        var finishSomeBlock = state.Function.AppendBasicBlock("read_line_finish_some");
        var returnNoneBlock = state.Function.AppendBasicBlock("read_line_return_none");
        var overflowBlock = state.Function.AppendBasicBlock("read_line_overflow");
        var continueBlock = state.Function.AppendBasicBlock("read_line_continue");

        builder.BuildBr(loopBlock);

        builder.PositionAtEnd(loopBlock);
        LLVMValueRef bytesRead = state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitSyscall(
                state,
                SyscallRead,
                LLVMValueRef.CreateConstInt(state.I64, 0, false),
                builder.BuildPtrToInt(byteSlot, state.I64, "read_line_byte_ptr"),
                LLVMValueRef.CreateConstInt(state.I64, 1, false),
                "sys_read_line")
            : EmitWindowsReadByte(state, stdinHandle, byteSlot, bytesReadSlot);
        LLVMValueRef hasByte = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, bytesRead, LLVMValueRef.CreateConstInt(state.I64, 0, false), "read_line_has_byte");
        builder.BuildCondBr(hasByte, inspectBlock, eofBlock);

        builder.PositionAtEnd(inspectBlock);
        LLVMValueRef currentByte = builder.BuildLoad2(state.I8, byteSlot, "read_line_current_byte");
        LLVMValueRef isLf = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, currentByte, LLVMValueRef.CreateConstInt(state.I8, 10, false), "read_line_is_lf");
        builder.BuildCondBr(isLf, finishSomeBlock, skipCrBlock);

        builder.PositionAtEnd(skipCrBlock);
        LLVMValueRef isCr = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, currentByte, LLVMValueRef.CreateConstInt(state.I8, 13, false), "read_line_is_cr");
        builder.BuildCondBr(isCr, loopBlock, storeByteBlock);

        builder.PositionAtEnd(storeByteBlock);
        LLVMValueRef currentLen = builder.BuildLoad2(state.I64, lenSlot, "read_line_len_value");
        LLVMValueRef atCapacity = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, currentLen, LLVMValueRef.CreateConstInt(state.I64, InputBufSize, false), "read_line_at_capacity");
        builder.BuildCondBr(atCapacity, overflowBlock, appendByteBlock);

        builder.PositionAtEnd(appendByteBlock);
        LLVMValueRef destPtr = builder.BuildGEP2(state.I8, inputBufPtr, new[] { currentLen }, "read_line_dest_ptr");
        builder.BuildStore(currentByte, destPtr);
        builder.BuildStore(builder.BuildAdd(currentLen, LLVMValueRef.CreateConstInt(state.I64, 1, false), "read_line_len_next"), lenSlot);
        builder.BuildBr(loopBlock);

        builder.PositionAtEnd(eofBlock);
        LLVMValueRef lenAtEof = builder.BuildLoad2(state.I64, lenSlot, "read_line_len_at_eof");
        LLVMValueRef isEmpty = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, lenAtEof, LLVMValueRef.CreateConstInt(state.I64, 0, false), "read_line_is_empty");
        builder.BuildCondBr(isEmpty, returnNoneBlock, finishSomeBlock);

        builder.PositionAtEnd(finishSomeBlock);
        LLVMValueRef finalLen = builder.BuildLoad2(state.I64, lenSlot, "read_line_final_len");
        LLVMValueRef stringRef = EmitAllocDynamic(state, builder.BuildAdd(finalLen, LLVMValueRef.CreateConstInt(state.I64, 8, false), "read_line_string_bytes"));
        StoreMemory(state, stringRef, 0, finalLen, "read_line_string_len");
        EmitCopyBytes(state, GetStringBytesPointer(state, stringRef, "read_line_string_dest"), inputBufPtr, finalLen, "read_line_copy_bytes");
        LLVMValueRef someRef = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, someRef, 8, stringRef, "read_line_some_value");
        builder.BuildStore(someRef, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(returnNoneBlock);
        builder.BuildStore(EmitAllocAdt(state, 0, 0), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(overflowBlock);
        EmitPanic(state, EmitStackStringObject(state, "readLine input too long"));

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "read_line_result_value");
    }

    private static LLVMValueRef EmitFileReadText(LlvmCodegenState state, LLVMValueRef pathRef)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxFileReadText(state, pathRef)
            : EmitWindowsFileReadText(state, pathRef);
    }

    private static LLVMValueRef EmitFileWriteText(LlvmCodegenState state, LLVMValueRef pathRef, LLVMValueRef textRef)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxFileWriteText(state, pathRef, textRef)
            : EmitWindowsFileWriteText(state, pathRef, textRef);
    }

    private static LLVMValueRef EmitFileExists(LlvmCodegenState state, LLVMValueRef pathRef)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxFileExists(state, pathRef)
            : EmitWindowsFileExists(state, pathRef);
    }

    private static LLVMValueRef EmitLinuxFileReadText(LlvmCodegenState state, LLVMValueRef pathRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef pathCstr = EmitStringToCString(state, pathRef, "fs_read_path");
        LLVMValueRef fdSlot = builder.BuildAlloca(state.I64, "fs_read_fd");
        LLVMValueRef stringSlot = builder.BuildAlloca(state.I64, "fs_read_string");
        LLVMValueRef remainingSlot = builder.BuildAlloca(state.I64, "fs_read_remaining");
        LLVMValueRef cursorSlot = builder.BuildAlloca(state.I64, "fs_read_cursor");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "fs_read_result");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), fdSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);

        var openBlock = state.Function.AppendBasicBlock("fs_read_open");
        var seekEndBlock = state.Function.AppendBasicBlock("fs_read_seek_end");
        var seekStartBlock = state.Function.AppendBasicBlock("fs_read_seek_start");
        var allocBlock = state.Function.AppendBasicBlock("fs_read_alloc");
        var readCheckBlock = state.Function.AppendBasicBlock("fs_read_loop_check");
        var readBodyBlock = state.Function.AppendBasicBlock("fs_read_loop_body");
        var readDoneBlock = state.Function.AppendBasicBlock("fs_read_done");
        var utf8CheckBlock = state.Function.AppendBasicBlock("fs_read_utf8_check");
        var closeOkBlock = state.Function.AppendBasicBlock("fs_read_close_ok");
        var closeInvalidBlock = state.Function.AppendBasicBlock("fs_read_close_invalid");
        var closeErrorBlock = state.Function.AppendBasicBlock("fs_read_close_error");
        var maybeCloseErrorBlock = state.Function.AppendBasicBlock("fs_read_maybe_close_error");
        var closeHandleBlock = state.Function.AppendBasicBlock("fs_read_close_handle");
        var returnErrorBlock = state.Function.AppendBasicBlock("fs_read_return_error");
        var continueBlock = state.Function.AppendBasicBlock("fs_read_continue");

        builder.BuildBr(openBlock);

        builder.PositionAtEnd(openBlock);
        LLVMValueRef fd = EmitSyscall(
            state,
            SyscallOpen,
            builder.BuildPtrToInt(pathCstr, state.I64, "fs_read_path_ptr"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_read_open_call");
        builder.BuildStore(fd, fdSlot);
        LLVMValueRef openFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, fd, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_open_failed");
        builder.BuildCondBr(openFailed, returnErrorBlock, seekEndBlock);

        builder.PositionAtEnd(seekEndBlock);
        LLVMValueRef fileLength = EmitSyscall(
            state,
            SyscallLseek,
            fd,
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 2, false),
            "fs_read_seek_end_call");
        LLVMValueRef seekEndFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, fileLength, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_seek_end_failed");
        builder.BuildCondBr(seekEndFailed, maybeCloseErrorBlock, seekStartBlock);

        builder.PositionAtEnd(seekStartBlock);
        LLVMValueRef seekStart = EmitSyscall(
            state,
            SyscallLseek,
            fd,
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_read_seek_start_call");
        LLVMValueRef seekStartFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, seekStart, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_seek_start_failed");
        builder.BuildCondBr(seekStartFailed, maybeCloseErrorBlock, allocBlock);

        builder.PositionAtEnd(allocBlock);
        LLVMValueRef exceedsLimit = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, fileLength, LLVMValueRef.CreateConstInt(state.I64, MaxFileReadBytes, false), "fs_read_exceeds_limit");
        var withinLimitBlock = state.Function.AppendBasicBlock("fs_read_within_limit");
        builder.BuildCondBr(exceedsLimit, maybeCloseErrorBlock, withinLimitBlock);

        builder.PositionAtEnd(withinLimitBlock);
        LLVMValueRef stringRef = EmitAllocDynamic(state, builder.BuildAdd(fileLength, LLVMValueRef.CreateConstInt(state.I64, 8, false), "fs_read_total_bytes"));
        StoreMemory(state, stringRef, 0, fileLength, "fs_read_len");
        builder.BuildStore(stringRef, stringSlot);
        builder.BuildStore(fileLength, remainingSlot);
        builder.BuildStore(GetStringBytesAddress(state, stringRef, "fs_read_cursor_start"), cursorSlot);
        LLVMValueRef isEmpty = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, fileLength, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_empty");
        builder.BuildCondBr(isEmpty, utf8CheckBlock, readCheckBlock);

        builder.PositionAtEnd(readCheckBlock);
        LLVMValueRef remaining = builder.BuildLoad2(state.I64, remainingSlot, "fs_read_remaining_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, remaining, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_done");
        builder.BuildCondBr(done, utf8CheckBlock, readBodyBlock);

        builder.PositionAtEnd(readBodyBlock);
        LLVMValueRef cursorAddress = builder.BuildLoad2(state.I64, cursorSlot, "fs_read_cursor_value");
        LLVMValueRef readBytes = EmitSyscall(
            state,
            SyscallRead,
            builder.BuildLoad2(state.I64, fdSlot, "fs_read_fd_value"),
            cursorAddress,
            remaining,
            "fs_read_read_call");
        LLVMValueRef readFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, readBytes, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_failed");
        builder.BuildCondBr(readFailed, maybeCloseErrorBlock, readDoneBlock);

        builder.PositionAtEnd(readDoneBlock);
        builder.BuildStore(builder.BuildSub(remaining, readBytes, "fs_read_remaining_next"), remainingSlot);
        builder.BuildStore(builder.BuildAdd(cursorAddress, readBytes, "fs_read_cursor_next"), cursorSlot);
        builder.BuildBr(readCheckBlock);

        builder.PositionAtEnd(utf8CheckBlock);
        LLVMValueRef utf8Valid = EmitValidateUtf8(
            state,
            GetStringBytesPointer(state, builder.BuildLoad2(state.I64, stringSlot, "fs_read_string_value"), "fs_read_utf8_ptr"),
            LoadStringLength(state, builder.BuildLoad2(state.I64, stringSlot, "fs_read_string_len_value"), "fs_read_utf8_len"),
            "fs_read_utf8");
        LLVMValueRef isUtf8Valid = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, utf8Valid, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_is_utf8_valid");
        builder.BuildCondBr(isUtf8Valid, closeOkBlock, closeInvalidBlock);

        builder.PositionAtEnd(closeOkBlock);
        EmitSyscall(
            state,
            SyscallClose,
            builder.BuildLoad2(state.I64, fdSlot, "fs_read_close_fd"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_read_close_ok_call");
        builder.BuildStore(EmitResultOk(state, builder.BuildLoad2(state.I64, stringSlot, "fs_read_ok_value")), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(closeInvalidBlock);
        EmitSyscall(
            state,
            SyscallClose,
            builder.BuildLoad2(state.I64, fdSlot, "fs_read_invalid_fd"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_read_close_invalid_call");
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, FileReadInvalidUtf8Message)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(maybeCloseErrorBlock);
        LLVMValueRef fdValue = builder.BuildLoad2(state.I64, fdSlot, "fs_read_error_fd");
        LLVMValueRef shouldClose = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, fdValue, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_should_close");
        builder.BuildCondBr(shouldClose, closeHandleBlock, returnErrorBlock);

        builder.PositionAtEnd(closeHandleBlock);
        EmitSyscall(
            state,
            SyscallClose,
            builder.BuildLoad2(state.I64, fdSlot, "fs_read_close_error_fd"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_read_close_error_call");
        builder.BuildBr(returnErrorBlock);

        builder.PositionAtEnd(returnErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, FileReadFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(closeErrorBlock);
        builder.BuildBr(returnErrorBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "fs_read_result_value");
    }

    private static LLVMValueRef EmitLinuxFileWriteText(LlvmCodegenState state, LLVMValueRef pathRef, LLVMValueRef textRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef pathCstr = EmitStringToCString(state, pathRef, "fs_write_path");
        LLVMValueRef fdSlot = builder.BuildAlloca(state.I64, "fs_write_fd");
        LLVMValueRef remainingSlot = builder.BuildAlloca(state.I64, "fs_write_remaining");
        LLVMValueRef cursorSlot = builder.BuildAlloca(state.I64, "fs_write_cursor");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "fs_write_result");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), fdSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);

        var openBlock = state.Function.AppendBasicBlock("fs_write_open");
        var loopCheckBlock = state.Function.AppendBasicBlock("fs_write_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock("fs_write_loop_body");
        var advanceBlock = state.Function.AppendBasicBlock("fs_write_advance");
        var closeOkBlock = state.Function.AppendBasicBlock("fs_write_close_ok");
        var maybeCloseErrorBlock = state.Function.AppendBasicBlock("fs_write_maybe_close_error");
        var closeErrorBlock = state.Function.AppendBasicBlock("fs_write_close_error");
        var returnErrorBlock = state.Function.AppendBasicBlock("fs_write_return_error");
        var continueBlock = state.Function.AppendBasicBlock("fs_write_continue");

        builder.BuildBr(openBlock);

        builder.PositionAtEnd(openBlock);
        LLVMValueRef fd = EmitSyscall(
            state,
            SyscallOpen,
            builder.BuildPtrToInt(pathCstr, state.I64, "fs_write_path_ptr"),
            LLVMValueRef.CreateConstInt(state.I64, 0x241, false),
            LLVMValueRef.CreateConstInt(state.I64, 420, false),
            "fs_write_open_call");
        builder.BuildStore(fd, fdSlot);
        LLVMValueRef openFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, fd, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_write_open_failed");
        builder.BuildStore(LoadStringLength(state, textRef, "fs_write_text_len"), remainingSlot);
        builder.BuildStore(GetStringBytesAddress(state, textRef, "fs_write_text_ptr"), cursorSlot);
        builder.BuildCondBr(openFailed, returnErrorBlock, loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef remaining = builder.BuildLoad2(state.I64, remainingSlot, "fs_write_remaining_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, remaining, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_write_done");
        builder.BuildCondBr(done, closeOkBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef cursorAddress = builder.BuildLoad2(state.I64, cursorSlot, "fs_write_cursor_value");
        LLVMValueRef bytesWritten = EmitSyscall(
            state,
            SyscallWrite,
            builder.BuildLoad2(state.I64, fdSlot, "fs_write_fd_value"),
            cursorAddress,
            remaining,
            "fs_write_write_call");
        LLVMValueRef writeFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, bytesWritten, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_write_failed");
        builder.BuildCondBr(writeFailed, maybeCloseErrorBlock, advanceBlock);

        builder.PositionAtEnd(advanceBlock);
        builder.BuildStore(builder.BuildSub(remaining, bytesWritten, "fs_write_remaining_next"), remainingSlot);
        builder.BuildStore(builder.BuildAdd(cursorAddress, bytesWritten, "fs_write_cursor_next"), cursorSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(closeOkBlock);
        EmitSyscall(
            state,
            SyscallClose,
            builder.BuildLoad2(state.I64, fdSlot, "fs_write_close_fd"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_write_close_ok_call");
        builder.BuildStore(EmitResultOk(state, EmitUnitValue(state)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(maybeCloseErrorBlock);
        LLVMValueRef fdValue = builder.BuildLoad2(state.I64, fdSlot, "fs_write_error_fd");
        LLVMValueRef shouldClose = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, fdValue, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_write_should_close");
        builder.BuildCondBr(shouldClose, closeErrorBlock, returnErrorBlock);

        builder.PositionAtEnd(closeErrorBlock);
        EmitSyscall(
            state,
            SyscallClose,
            builder.BuildLoad2(state.I64, fdSlot, "fs_write_close_error_fd"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_write_close_error_call");
        builder.BuildBr(returnErrorBlock);

        builder.PositionAtEnd(returnErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, FileWriteFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "fs_write_result_value");
    }

    private static LLVMValueRef EmitLinuxFileExists(LlvmCodegenState state, LLVMValueRef pathRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef pathCstr = EmitStringToCString(state, pathRef, "fs_exists_path");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "fs_exists_result");
        var openBlock = state.Function.AppendBasicBlock("fs_exists_open");
        var foundBlock = state.Function.AppendBasicBlock("fs_exists_found");
        var missingBlock = state.Function.AppendBasicBlock("fs_exists_missing");
        var continueBlock = state.Function.AppendBasicBlock("fs_exists_continue");

        builder.BuildBr(openBlock);

        builder.PositionAtEnd(openBlock);
        LLVMValueRef fd = EmitSyscall(
            state,
            SyscallOpen,
            builder.BuildPtrToInt(pathCstr, state.I64, "fs_exists_path_ptr"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_exists_open_call");
        LLVMValueRef openFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, fd, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_exists_open_failed");
        builder.BuildCondBr(openFailed, missingBlock, foundBlock);

        builder.PositionAtEnd(foundBlock);
        EmitSyscall(
            state,
            SyscallClose,
            fd,
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_exists_close_call");
        builder.BuildStore(EmitResultOk(state, LLVMValueRef.CreateConstInt(state.I64, 1, false)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(missingBlock);
        builder.BuildStore(EmitResultOk(state, LLVMValueRef.CreateConstInt(state.I64, 0, false)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "fs_exists_result_value");
    }

    private static LLVMValueRef EmitWindowsFileReadText(LlvmCodegenState state, LLVMValueRef pathRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef pathCstr = EmitStringToCString(state, pathRef, "fs_read_path");
        LLVMValueRef handleSlot = builder.BuildAlloca(state.I64, "fs_read_handle");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "fs_read_result");
        LLVMValueRef bytesReadSlot = builder.BuildAlloca(state.I32, "fs_read_bytes_read");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), handleSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);

        var openBlock = state.Function.AppendBasicBlock("fs_read_win_open");
        var readBlock = state.Function.AppendBasicBlock("fs_read_win_read");
        var utf8Block = state.Function.AppendBasicBlock("fs_read_win_utf8");
        var closeOkBlock = state.Function.AppendBasicBlock("fs_read_win_close_ok");
        var closeInvalidBlock = state.Function.AppendBasicBlock("fs_read_win_close_invalid");
        var closeErrorBlock = state.Function.AppendBasicBlock("fs_read_win_close_error");
        var returnErrorBlock = state.Function.AppendBasicBlock("fs_read_win_return_error");
        var continueBlock = state.Function.AppendBasicBlock("fs_read_win_continue");

        builder.BuildBr(openBlock);

        builder.PositionAtEnd(openBlock);
        LLVMValueRef handle = EmitWindowsCreateFile(
            state,
            pathCstr,
            unchecked((int)0x80000000),
            1,
            3,
            "fs_read_create_file");
        builder.BuildStore(handle, handleSlot);
        LLVMValueRef openFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, handle, LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), "fs_read_handle_invalid");
        builder.BuildCondBr(openFailed, returnErrorBlock, readBlock);

        builder.PositionAtEnd(readBlock);
        LLVMValueRef stringRef = EmitAllocDynamic(state, LLVMValueRef.CreateConstInt(state.I64, MaxFileReadBytes + 8, false));
        StoreMemory(state, stringRef, 0, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_win_len_init");
        LLVMValueRef readSucceeded = EmitWindowsReadFile(
            state,
            builder.BuildLoad2(state.I64, handleSlot, "fs_read_handle_value"),
            GetStringBytesPointer(state, stringRef, "fs_read_win_bytes"),
            LLVMValueRef.CreateConstInt(state.I32, MaxFileReadBytes, false),
            bytesReadSlot,
            "fs_read_win_read_call");
        builder.BuildStore(builder.BuildZExt(builder.BuildLoad2(state.I32, bytesReadSlot, "fs_read_bytes_read_value"), state.I64, "fs_read_bytes_i64"), GetMemoryPointer(state, stringRef, 0, "fs_read_win_len_ptr"));
        builder.BuildCondBr(readSucceeded, utf8Block, closeErrorBlock);

        builder.PositionAtEnd(utf8Block);
        LLVMValueRef utf8Valid = EmitValidateUtf8(
            state,
            GetStringBytesPointer(state, stringRef, "fs_read_win_utf8_ptr"),
            LoadStringLength(state, stringRef, "fs_read_win_utf8_len"),
            "fs_read_win_utf8");
        LLVMValueRef isUtf8Valid = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, utf8Valid, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_win_is_utf8_valid");
        builder.BuildCondBr(isUtf8Valid, closeOkBlock, closeInvalidBlock);

        builder.PositionAtEnd(closeOkBlock);
        EmitWindowsCloseHandle(state, builder.BuildLoad2(state.I64, handleSlot, "fs_read_close_handle"), "fs_read_close_ok");
        builder.BuildStore(EmitResultOk(state, stringRef), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(closeInvalidBlock);
        EmitWindowsCloseHandle(state, builder.BuildLoad2(state.I64, handleSlot, "fs_read_invalid_handle"), "fs_read_close_invalid");
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, FileReadInvalidUtf8Message)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(closeErrorBlock);
        EmitWindowsCloseHandle(state, builder.BuildLoad2(state.I64, handleSlot, "fs_read_error_handle"), "fs_read_close_error");
        builder.BuildBr(returnErrorBlock);

        builder.PositionAtEnd(returnErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, FileReadFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "fs_read_win_result_value");
    }

    private static LLVMValueRef EmitWindowsFileWriteText(LlvmCodegenState state, LLVMValueRef pathRef, LLVMValueRef textRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef pathCstr = EmitStringToCString(state, pathRef, "fs_write_path");
        LLVMValueRef handleSlot = builder.BuildAlloca(state.I64, "fs_write_handle");
        LLVMValueRef remainingSlot = builder.BuildAlloca(state.I64, "fs_write_remaining");
        LLVMValueRef cursorSlot = builder.BuildAlloca(state.I64, "fs_write_cursor");
        LLVMValueRef bytesWrittenSlot = builder.BuildAlloca(state.I32, "fs_write_bytes_written");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "fs_write_result");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), handleSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);

        var openBlock = state.Function.AppendBasicBlock("fs_write_win_open");
        var loopCheckBlock = state.Function.AppendBasicBlock("fs_write_win_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock("fs_write_win_loop_body");
        var advanceBlock = state.Function.AppendBasicBlock("fs_write_win_advance");
        var closeOkBlock = state.Function.AppendBasicBlock("fs_write_win_close_ok");
        var closeErrorBlock = state.Function.AppendBasicBlock("fs_write_win_close_error");
        var returnErrorBlock = state.Function.AppendBasicBlock("fs_write_win_return_error");
        var continueBlock = state.Function.AppendBasicBlock("fs_write_win_continue");

        builder.BuildBr(openBlock);

        builder.PositionAtEnd(openBlock);
        LLVMValueRef handle = EmitWindowsCreateFile(
            state,
            pathCstr,
            0x40000000,
            0,
            2,
            "fs_write_create_file");
        builder.BuildStore(handle, handleSlot);
        builder.BuildStore(LoadStringLength(state, textRef, "fs_write_win_text_len"), remainingSlot);
        builder.BuildStore(GetStringBytesAddress(state, textRef, "fs_write_win_text_ptr"), cursorSlot);
        LLVMValueRef openFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, handle, LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), "fs_write_handle_invalid");
        builder.BuildCondBr(openFailed, returnErrorBlock, loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef remaining = builder.BuildLoad2(state.I64, remainingSlot, "fs_write_win_remaining_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, remaining, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_write_win_done");
        builder.BuildCondBr(done, closeOkBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef chunkSize = builder.BuildSelect(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, remaining, LLVMValueRef.CreateConstInt(state.I64, uint.MaxValue, false), "fs_write_win_chunk_gt"),
            LLVMValueRef.CreateConstInt(state.I64, uint.MaxValue, false),
            remaining,
            "fs_write_win_chunk_size");
        LLVMValueRef wrote = EmitWindowsWriteFile(
            state,
            builder.BuildLoad2(state.I64, handleSlot, "fs_write_handle_value"),
            builder.BuildIntToPtr(builder.BuildLoad2(state.I64, cursorSlot, "fs_write_cursor_value"), state.I8Ptr, "fs_write_cursor_ptr"),
            builder.BuildTrunc(chunkSize, state.I32, "fs_write_chunk_i32"),
            bytesWrittenSlot,
            "fs_write_win_write_call");
        builder.BuildCondBr(wrote, advanceBlock, closeErrorBlock);

        builder.PositionAtEnd(advanceBlock);
        LLVMValueRef bytesWritten = builder.BuildZExt(builder.BuildLoad2(state.I32, bytesWrittenSlot, "fs_write_bytes_written_value"), state.I64, "fs_write_bytes_written_i64");
        LLVMValueRef wroteZero = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, bytesWritten, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_write_wrote_zero");
        var zeroWriteBlock = state.Function.AppendBasicBlock("fs_write_win_zero");
        var updateBlock = state.Function.AppendBasicBlock("fs_write_win_update");
        builder.BuildCondBr(wroteZero, zeroWriteBlock, updateBlock);

        builder.PositionAtEnd(zeroWriteBlock);
        builder.BuildBr(closeErrorBlock);

        builder.PositionAtEnd(updateBlock);
        LLVMValueRef cursorValue = builder.BuildLoad2(state.I64, cursorSlot, "fs_write_cursor_current");
        builder.BuildStore(builder.BuildSub(remaining, bytesWritten, "fs_write_remaining_next"), remainingSlot);
        builder.BuildStore(builder.BuildAdd(cursorValue, bytesWritten, "fs_write_cursor_next"), cursorSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(closeOkBlock);
        EmitWindowsCloseHandle(state, builder.BuildLoad2(state.I64, handleSlot, "fs_write_close_handle"), "fs_write_close_ok");
        builder.BuildStore(EmitResultOk(state, EmitUnitValue(state)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(closeErrorBlock);
        EmitWindowsCloseHandle(state, builder.BuildLoad2(state.I64, handleSlot, "fs_write_error_handle"), "fs_write_close_error");
        builder.BuildBr(returnErrorBlock);

        builder.PositionAtEnd(returnErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, FileWriteFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "fs_write_win_result_value");
    }

    private static LLVMValueRef EmitWindowsFileExists(LlvmCodegenState state, LLVMValueRef pathRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef pathCstr = EmitStringToCString(state, pathRef, "fs_exists_path");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "fs_exists_win_result");
        var checkBlock = state.Function.AppendBasicBlock("fs_exists_win_check");
        var missingBlock = state.Function.AppendBasicBlock("fs_exists_win_missing");
        var foundBlock = state.Function.AppendBasicBlock("fs_exists_win_found");
        var continueBlock = state.Function.AppendBasicBlock("fs_exists_win_continue");

        builder.BuildBr(checkBlock);

        builder.PositionAtEnd(checkBlock);
        LLVMValueRef attrs = EmitWindowsGetFileAttributes(state, pathCstr, "fs_exists_get_attrs");
        LLVMValueRef missing = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, attrs, LLVMValueRef.CreateConstInt(state.I32, uint.MaxValue, false), "fs_exists_missing");
        builder.BuildCondBr(missing, missingBlock, foundBlock);

        builder.PositionAtEnd(foundBlock);
        builder.BuildStore(EmitResultOk(state, LLVMValueRef.CreateConstInt(state.I64, 1, false)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(missingBlock);
        builder.BuildStore(EmitResultOk(state, LLVMValueRef.CreateConstInt(state.I64, 0, false)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "fs_exists_win_result_value");
    }

    private static LLVMValueRef EmitStringToCString(LlvmCodegenState state, LLVMValueRef stringRef, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef len = LoadStringLength(state, stringRef, prefix + "_len");
        LLVMValueRef cstrRef = EmitAllocDynamic(state, builder.BuildAdd(len, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_size"));
        LLVMValueRef destPtr = builder.BuildIntToPtr(cstrRef, state.I8Ptr, prefix + "_dest");
        EmitCopyBytes(state, destPtr, GetStringBytesPointer(state, stringRef, prefix + "_src"), len, prefix + "_copy");
        LLVMValueRef terminatorPtr = builder.BuildGEP2(state.I8, destPtr, new[] { len }, prefix + "_nul_ptr");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I8, 0, false), terminatorPtr);
        return destPtr;
    }

    private static LLVMValueRef EmitUnitValue(LlvmCodegenState state)
    {
        return EmitAllocAdt(state, 0, 0);
    }

    private static LLVMValueRef EmitResultOk(LlvmCodegenState state, LLVMValueRef value)
    {
        LLVMValueRef result = EmitAllocAdt(state, 0, 1);
        StoreMemory(state, result, 8, value, "result_ok_value");
        return result;
    }

    private static LLVMValueRef EmitResultError(LlvmCodegenState state, LLVMValueRef errorStringRef)
    {
        LLVMValueRef result = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, result, 8, errorStringRef, "result_error_value");
        return result;
    }

    private static LLVMValueRef EmitHeapStringLiteral(LlvmCodegenState state, string value)
    {
        return EmitHeapStringFromBytes(state, System.Text.Encoding.UTF8.GetBytes(value), "heap_string_literal");
    }

    private static LLVMValueRef EmitHeapStringFromBytes(LlvmCodegenState state, IReadOnlyList<byte> bytes, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef len = LLVMValueRef.CreateConstInt(state.I64, (ulong)bytes.Count, false);
        LLVMValueRef stringRef = EmitAllocDynamic(state, builder.BuildAdd(len, LLVMValueRef.CreateConstInt(state.I64, 8, false), prefix + "_size"));
        StoreMemory(state, stringRef, 0, len, prefix + "_len");
        LLVMValueRef destPtr = GetStringBytesPointer(state, stringRef, prefix + "_bytes");
        for (int i = 0; i < bytes.Count; i++)
        {
            LLVMValueRef cellPtr = builder.BuildGEP2(
                state.I8,
                destPtr,
                new[] { LLVMValueRef.CreateConstInt(state.I64, (ulong)i, false) },
                $"{prefix}_byte_ptr_{i}");
            builder.BuildStore(LLVMValueRef.CreateConstInt(state.I8, bytes[i], false), cellPtr);
        }

        return stringRef;
    }

    private static LLVMValueRef GetStringBytesAddress(LlvmCodegenState state, LLVMValueRef stringRef, string name)
    {
        return state.Target.Builder.BuildAdd(stringRef, LLVMValueRef.CreateConstInt(state.I64, 8, false), name);
    }

    private static LLVMValueRef EmitValidateUtf8(LlvmCodegenState state, LLVMValueRef bytesPtr, LLVMValueRef len, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_index");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, prefix + "_result");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);

        var loopBlock = state.Function.AppendBasicBlock(prefix + "_loop");
        var asciiBlock = state.Function.AppendBasicBlock(prefix + "_ascii");
        var twoBlock = state.Function.AppendBasicBlock(prefix + "_two");
        var threeBlock = state.Function.AppendBasicBlock(prefix + "_three");
        var e0Block = state.Function.AppendBasicBlock(prefix + "_e0");
        var edBlock = state.Function.AppendBasicBlock(prefix + "_ed");
        var f0Block = state.Function.AppendBasicBlock(prefix + "_f0");
        var fourBlock = state.Function.AppendBasicBlock(prefix + "_four");
        var f4Block = state.Function.AppendBasicBlock(prefix + "_f4");
        var validBlock = state.Function.AppendBasicBlock(prefix + "_valid");
        var invalidBlock = state.Function.AppendBasicBlock(prefix + "_invalid");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");

        builder.BuildBr(loopBlock);

        builder.PositionAtEnd(loopBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, index, len, prefix + "_done");
        var inspectBlock = state.Function.AppendBasicBlock(prefix + "_inspect");
        builder.BuildCondBr(done, validBlock, inspectBlock);

        builder.PositionAtEnd(inspectBlock);
        LLVMValueRef firstByte = LoadByteAt(state, bytesPtr, index, prefix + "_byte0");
        LLVMValueRef firstByte64 = builder.BuildZExt(firstByte, state.I64, prefix + "_byte0_i64");
        LLVMValueRef isAscii = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0x80, false), prefix + "_is_ascii");
        var nonAsciiBlock = state.Function.AppendBasicBlock(prefix + "_non_ascii");
        builder.BuildCondBr(isAscii, asciiBlock, nonAsciiBlock);

        builder.PositionAtEnd(nonAsciiBlock);
        LLVMValueRef ltC2 = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xC2, false), prefix + "_lt_c2");
        var geC2Block = state.Function.AppendBasicBlock(prefix + "_ge_c2");
        builder.BuildCondBr(ltC2, invalidBlock, geC2Block);

        builder.PositionAtEnd(geC2Block);
        LLVMValueRef leDf = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xDF, false), prefix + "_le_df");
        var gtDfBlock = state.Function.AppendBasicBlock(prefix + "_gt_df");
        builder.BuildCondBr(leDf, twoBlock, gtDfBlock);

        builder.PositionAtEnd(gtDfBlock);
        LLVMValueRef isE0 = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xE0, false), prefix + "_is_e0");
        var afterE0Block = state.Function.AppendBasicBlock(prefix + "_after_e0");
        builder.BuildCondBr(isE0, e0Block, afterE0Block);

        builder.PositionAtEnd(afterE0Block);
        LLVMValueRef leEc = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xEC, false), prefix + "_le_ec");
        var afterEcBlock = state.Function.AppendBasicBlock(prefix + "_after_ec");
        builder.BuildCondBr(leEc, threeBlock, afterEcBlock);

        builder.PositionAtEnd(afterEcBlock);
        LLVMValueRef isEd = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xED, false), prefix + "_is_ed");
        var afterEdBlock = state.Function.AppendBasicBlock(prefix + "_after_ed");
        builder.BuildCondBr(isEd, edBlock, afterEdBlock);

        builder.PositionAtEnd(afterEdBlock);
        LLVMValueRef leEf = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xEF, false), prefix + "_le_ef");
        var afterEfBlock = state.Function.AppendBasicBlock(prefix + "_after_ef");
        builder.BuildCondBr(leEf, threeBlock, afterEfBlock);

        builder.PositionAtEnd(afterEfBlock);
        LLVMValueRef isF0 = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xF0, false), prefix + "_is_f0");
        var afterF0Block = state.Function.AppendBasicBlock(prefix + "_after_f0");
        builder.BuildCondBr(isF0, f0Block, afterF0Block);

        builder.PositionAtEnd(afterF0Block);
        LLVMValueRef leF3 = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xF3, false), prefix + "_le_f3");
        var afterF3Block = state.Function.AppendBasicBlock(prefix + "_after_f3");
        builder.BuildCondBr(leF3, fourBlock, afterF3Block);

        builder.PositionAtEnd(afterF3Block);
        LLVMValueRef isF4 = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, firstByte64, LLVMValueRef.CreateConstInt(state.I64, 0xF4, false), prefix + "_is_f4");
        builder.BuildCondBr(isF4, f4Block, invalidBlock);

        builder.PositionAtEnd(asciiBlock);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_ascii_next"), indexSlot);
        builder.BuildBr(loopBlock);

        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 2, 0x80, 0xBF, prefix + "_two", twoBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 3, 0x80, 0xBF, prefix + "_three", threeBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 3, 0xA0, 0xBF, prefix + "_e0", e0Block, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 3, 0x80, 0x9F, prefix + "_ed", edBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 4, 0x90, 0xBF, prefix + "_f0", f0Block, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 4, 0x80, 0xBF, prefix + "_four", fourBlock, loopBlock, invalidBlock);
        EmitUtf8SequenceValidation(state, bytesPtr, len, indexSlot, 4, 0x80, 0x8F, prefix + "_f4", f4Block, loopBlock, invalidBlock);

        builder.PositionAtEnd(validBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 1, false), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(invalidBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, prefix + "_result_value");
    }

    private static void EmitUtf8SequenceValidation(
        LlvmCodegenState state,
        LLVMValueRef bytesPtr,
        LLVMValueRef len,
        LLVMValueRef indexSlot,
        int sequenceLength,
        int secondByteMin,
        int secondByteMax,
        string prefix,
        LLVMBasicBlockRef entryBlock,
        LLVMBasicBlockRef successBlock,
        LLVMBasicBlockRef invalidBlock)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        builder.PositionAtEnd(entryBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index_value");
        LLVMValueRef remaining = builder.BuildSub(len, index, prefix + "_remaining");
        LLVMValueRef enoughBytes = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, remaining, LLVMValueRef.CreateConstInt(state.I64, (ulong)sequenceLength, false), prefix + "_enough");
        var bodyBlock = state.Function.AppendBasicBlock(prefix + "_body");
        builder.BuildCondBr(enoughBytes, bodyBlock, invalidBlock);

        builder.PositionAtEnd(bodyBlock);
        LLVMValueRef secondByte = LoadByteAt(state, bytesPtr, builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_second_index"), prefix + "_second_byte");
        LLVMValueRef secondByte64 = builder.BuildZExt(secondByte, state.I64, prefix + "_second_i64");
        LLVMValueRef secondInRange = BuildByteRangeCheck(state, secondByte64, secondByteMin, secondByteMax, prefix + "_second_range");
        LLVMBasicBlockRef nextBlock = bodyBlock;
        for (int offset = 2; offset < sequenceLength; offset++)
        {
            var checkBlock = state.Function.AppendBasicBlock(prefix + "_cont_" + offset);
            builder.BuildCondBr(secondInRange, checkBlock, invalidBlock);
            builder.PositionAtEnd(checkBlock);
            nextBlock = checkBlock;
            LLVMValueRef extraByte = LoadByteAt(state, bytesPtr, builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, (ulong)offset, false), prefix + "_idx_" + offset), prefix + "_byte_" + offset);
            LLVMValueRef extraByte64 = builder.BuildZExt(extraByte, state.I64, prefix + "_byte_i64_" + offset);
            LLVMValueRef extraInRange = BuildByteRangeCheck(state, extraByte64, 0x80, 0xBF, prefix + "_range_" + offset);
            secondInRange = extraInRange;
        }

        builder.PositionAtEnd(nextBlock);
        LLVMBasicBlockRef advanceBlock = state.Function.AppendBasicBlock(prefix + "_advance");
        builder.BuildCondBr(secondInRange, advanceBlock, invalidBlock);
        builder.PositionAtEnd(advanceBlock);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, (ulong)sequenceLength, false), prefix + "_next"), indexSlot);
        builder.BuildBr(successBlock);
    }

    private static LLVMValueRef BuildByteRangeCheck(LlvmCodegenState state, LLVMValueRef byteValue, int minInclusive, int maxInclusive, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef geMin = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, byteValue, LLVMValueRef.CreateConstInt(state.I64, (ulong)minInclusive, false), prefix + "_ge_min");
        LLVMValueRef leMax = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, byteValue, LLVMValueRef.CreateConstInt(state.I64, (ulong)maxInclusive, false), prefix + "_le_max");
        return builder.BuildAnd(geMin, leMax, prefix + "_in_range");
    }

    private static LLVMValueRef LoadByteAt(LlvmCodegenState state, LLVMValueRef bytesPtr, LLVMValueRef index, string name)
    {
        LLVMValueRef bytePtr = state.Target.Builder.BuildGEP2(state.I8, bytesPtr, new[] { index }, name + "_ptr");
        return state.Target.Builder.BuildLoad2(state.I8, bytePtr, name);
    }

    private static LLVMValueRef EmitTcpConnect(LlvmCodegenState state, LLVMValueRef hostRef, LLVMValueRef port)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxTcpConnect(state, hostRef, port)
            : EmitWindowsTcpConnect(state, hostRef, port);
    }

    private static LLVMValueRef EmitTcpSend(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef textRef)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxTcpSend(state, socket, textRef)
            : EmitWindowsTcpSend(state, socket, textRef);
    }

    private static LLVMValueRef EmitTcpReceive(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef maxBytes)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxTcpReceive(state, socket, maxBytes)
            : EmitWindowsTcpReceive(state, socket, maxBytes);
    }

    private static LLVMValueRef EmitTcpClose(LlvmCodegenState state, LLVMValueRef socket)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxTcpClose(state, socket)
            : EmitWindowsTcpClose(state, socket);
    }

    private static LLVMValueRef EmitHttpRequest(LlvmCodegenState state, LLVMValueRef urlRef, LLVMValueRef bodyRef, bool hasBody)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "http_result");
        LLVMValueRef hostSlot = builder.BuildAlloca(state.I64, "http_host");
        LLVMValueRef pathSlot = builder.BuildAlloca(state.I64, "http_path");
        LLVMValueRef portSlot = builder.BuildAlloca(state.I64, "http_port");
        LLVMValueRef responseSlot = builder.BuildAlloca(state.I64, "http_response");
        LLVMValueRef socketSlot = builder.BuildAlloca(state.I64, "http_socket");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, "http_index");
        LLVMValueRef hostStartSlot = builder.BuildAlloca(state.I64, "http_host_start");
        LLVMValueRef hostEndSlot = builder.BuildAlloca(state.I64, "http_host_end");
        LLVMValueRef pathStartSlot = builder.BuildAlloca(state.I64, "http_path_start");
        LLVMValueRef pathLenSlot = builder.BuildAlloca(state.I64, "http_path_len");
        LLVMValueRef portValueSlot = builder.BuildAlloca(state.I64, "http_port_value");
        LLVMValueRef portDigitsSlot = builder.BuildAlloca(state.I64, "http_port_digits");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), hostSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), pathSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 80, false), portSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), responseSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), socketSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 7, false), hostStartSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), hostEndSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), pathStartSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), pathLenSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 80, false), portValueSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), portDigitsSlot);

        LLVMValueRef urlLen = LoadStringLength(state, urlRef, "http_url_len");
        LLVMValueRef urlBytes = GetStringBytesPointer(state, urlRef, "http_url_bytes");

        var httpsCheckBlock = state.Function.AppendBasicBlock("http_https_check");
        var httpCheckBlock = state.Function.AppendBasicBlock("http_http_check");
        var scanHostSetupBlock = state.Function.AppendBasicBlock("http_scan_host_setup");
        var scanHostBlock = state.Function.AppendBasicBlock("http_scan_host");
        var parsePortBlock = state.Function.AppendBasicBlock("http_parse_port");
        var parsePortLoopBlock = state.Function.AppendBasicBlock("http_parse_port_loop");
        var parsePortInspectBlock = state.Function.AppendBasicBlock("http_parse_port_inspect");
        var havePathBlock = state.Function.AppendBasicBlock("http_have_path");
        var defaultPathBlock = state.Function.AppendBasicBlock("http_default_path");
        var connectBlock = state.Function.AppendBasicBlock("http_connect");
        var sendBlock = state.Function.AppendBasicBlock("http_send");
        var recvLoopBlock = state.Function.AppendBasicBlock("http_recv_loop");
        var recvInspectBlock = state.Function.AppendBasicBlock("http_recv_inspect");
        var recvDoneBlock = state.Function.AppendBasicBlock("http_recv_done");
        var parseResponseBlock = state.Function.AppendBasicBlock("http_parse_response");
        var httpsErrorBlock = state.Function.AppendBasicBlock("http_https_error");
        var closeErrorBlock = state.Function.AppendBasicBlock("http_close_error");
        var malformedResponseBlock = state.Function.AppendBasicBlock("http_malformed_response");
        var chunkedErrorBlock = state.Function.AppendBasicBlock("http_chunked_error");
        var continueBlock = state.Function.AppendBasicBlock("http_continue");

        builder.BuildBr(httpsCheckBlock);

        builder.PositionAtEnd(httpsCheckBlock);
        LLVMValueRef httpsPrefix = EmitHeapStringLiteral(state, "https://");
        LLVMValueRef isHttps = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntNE,
            EmitStartsWith(state, urlRef, httpsPrefix, "http_is_https"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "http_is_https_bool");
        builder.BuildCondBr(isHttps, httpsErrorBlock, httpCheckBlock);

        builder.PositionAtEnd(httpCheckBlock);
        LLVMValueRef httpPrefix = EmitHeapStringLiteral(state, "http://");
        LLVMValueRef isHttp = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntNE,
            EmitStartsWith(state, urlRef, httpPrefix, "http_is_http"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "http_is_http_bool");
        var malformedUrlBlock = state.Function.AppendBasicBlock("http_malformed_url");
        builder.BuildCondBr(isHttp, scanHostSetupBlock, malformedUrlBlock);

        builder.PositionAtEnd(malformedUrlBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedUrlMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(scanHostSetupBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 7, false), indexSlot);
        builder.BuildBr(scanHostBlock);

        builder.PositionAtEnd(scanHostBlock);
        LLVMValueRef hostLoopIndex = builder.BuildLoad2(state.I64, indexSlot, "http_host_loop_index");
        LLVMValueRef hostLoopDone = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, hostLoopIndex, urlLen, "http_host_loop_done");
        var hostInspectBlock = state.Function.AppendBasicBlock("http_host_inspect");
        builder.BuildCondBr(hostLoopDone, defaultPathBlock, hostInspectBlock);

        builder.PositionAtEnd(hostInspectBlock);
        LLVMValueRef hostByte = LoadByteAt(state, urlBytes, hostLoopIndex, "http_host_byte");
        LLVMValueRef isColon = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hostByte, LLVMValueRef.CreateConstInt(state.I8, (byte)':', false), "http_host_is_colon");
        var hostCheckSlashBlock = state.Function.AppendBasicBlock("http_host_check_slash");
        builder.BuildCondBr(isColon, parsePortBlock, hostCheckSlashBlock);

        builder.PositionAtEnd(hostCheckSlashBlock);
        LLVMValueRef isSlash = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hostByte, LLVMValueRef.CreateConstInt(state.I8, (byte)'/', false), "http_host_is_slash");
        var hostRejectBlock = state.Function.AppendBasicBlock("http_host_reject");
        var hostAdvanceBlock = state.Function.AppendBasicBlock("http_host_advance");
        builder.BuildCondBr(isSlash, defaultPathBlock, hostRejectBlock);

        builder.PositionAtEnd(hostRejectBlock);
        LLVMValueRef isQuestion = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hostByte, LLVMValueRef.CreateConstInt(state.I8, (byte)'?', false), "http_host_is_question");
        var hostHashCheckBlock = state.Function.AppendBasicBlock("http_host_hash_check");
        builder.BuildCondBr(isQuestion, malformedUrlBlock, hostHashCheckBlock);

        builder.PositionAtEnd(hostHashCheckBlock);
        LLVMValueRef isHash = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hostByte, LLVMValueRef.CreateConstInt(state.I8, (byte)'#', false), "http_host_is_hash");
        builder.BuildCondBr(isHash, malformedUrlBlock, hostAdvanceBlock);

        builder.PositionAtEnd(hostAdvanceBlock);
        builder.BuildStore(builder.BuildAdd(hostLoopIndex, LLVMValueRef.CreateConstInt(state.I64, 1, false), "http_host_index_next"), indexSlot);
        builder.BuildBr(scanHostBlock);

        builder.PositionAtEnd(parsePortBlock);
        LLVMValueRef hostEnd = builder.BuildLoad2(state.I64, indexSlot, "http_host_end");
        LLVMValueRef hostLenValue = builder.BuildSub(hostEnd, LLVMValueRef.CreateConstInt(state.I64, 7, false), "http_host_len_before_port");
        LLVMValueRef missingHost = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hostLenValue, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_missing_host");
        var parsePortSetupBlock = state.Function.AppendBasicBlock("http_parse_port_setup");
        builder.BuildCondBr(missingHost, malformedUrlBlock, parsePortSetupBlock);

        builder.PositionAtEnd(parsePortSetupBlock);
        builder.BuildStore(hostEnd, hostEndSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), portValueSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), portDigitsSlot);
        builder.BuildStore(builder.BuildAdd(hostEnd, LLVMValueRef.CreateConstInt(state.I64, 1, false), "http_port_index_start"), indexSlot);
        builder.BuildBr(parsePortLoopBlock);

        builder.PositionAtEnd(parsePortLoopBlock);
        LLVMValueRef portIndex = builder.BuildLoad2(state.I64, indexSlot, "http_port_index");
        LLVMValueRef portDone = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, portIndex, urlLen, "http_port_done");
        builder.BuildCondBr(portDone, defaultPathBlock, parsePortInspectBlock);

        builder.PositionAtEnd(parsePortInspectBlock);
        LLVMValueRef portByte = LoadByteAt(state, urlBytes, portIndex, "http_port_byte");
        LLVMValueRef portIsSlash = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, portByte, LLVMValueRef.CreateConstInt(state.I8, (byte)'/', false), "http_port_is_slash");
        var portDigitCheckBlock = state.Function.AppendBasicBlock("http_port_digit_check");
        builder.BuildCondBr(portIsSlash, defaultPathBlock, portDigitCheckBlock);

        builder.PositionAtEnd(portDigitCheckBlock);
        LLVMValueRef portDigitValue = builder.BuildZExt(portByte, state.I64, "http_port_digit_value");
        LLVMValueRef portIsDigit = BuildByteRangeCheck(state, portDigitValue, (byte)'0', (byte)'9', "http_port_digit_range");
        var portAdvanceBlock = state.Function.AppendBasicBlock("http_port_advance");
        builder.BuildCondBr(portIsDigit, portAdvanceBlock, malformedUrlBlock);

        builder.PositionAtEnd(portAdvanceBlock);
        LLVMValueRef currentPort = builder.BuildLoad2(state.I64, portValueSlot, "http_port_current");
        LLVMValueRef parsedDigit = builder.BuildSub(portDigitValue, LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), "http_parsed_digit");
        LLVMValueRef nextPort = builder.BuildAdd(builder.BuildMul(currentPort, LLVMValueRef.CreateConstInt(state.I64, 10, false), "http_port_mul"), parsedDigit, "http_port_next");
        LLVMValueRef tooLargePort = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, nextPort, LLVMValueRef.CreateConstInt(state.I64, 65535, false), "http_port_too_large");
        var storePortBlock = state.Function.AppendBasicBlock("http_store_port");
        builder.BuildCondBr(tooLargePort, malformedUrlBlock, storePortBlock);

        builder.PositionAtEnd(storePortBlock);
        builder.BuildStore(nextPort, portValueSlot);
        builder.BuildStore(builder.BuildAdd(builder.BuildLoad2(state.I64, portDigitsSlot, "http_port_digits_value"), LLVMValueRef.CreateConstInt(state.I64, 1, false), "http_port_digits_next"), portDigitsSlot);
        builder.BuildStore(builder.BuildAdd(portIndex, LLVMValueRef.CreateConstInt(state.I64, 1, false), "http_port_index_next"), indexSlot);
        builder.BuildBr(parsePortLoopBlock);

        builder.PositionAtEnd(defaultPathBlock);
        LLVMValueRef finalHostEnd = builder.BuildLoad2(state.I64, hostEndSlot, "http_final_host_end");
        LLVMValueRef hostEndUnset = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, finalHostEnd, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_host_end_unset");
        var setHostEndBlock = state.Function.AppendBasicBlock("http_set_host_end");
        var buildHostBlock = state.Function.AppendBasicBlock("http_build_host");
        builder.BuildCondBr(hostEndUnset, setHostEndBlock, buildHostBlock);

        builder.PositionAtEnd(setHostEndBlock);
        LLVMValueRef currentIndex = builder.BuildLoad2(state.I64, indexSlot, "http_current_index");
        LLVMValueRef hostLenAtEnd = builder.BuildSub(currentIndex, LLVMValueRef.CreateConstInt(state.I64, 7, false), "http_host_len_at_end");
        LLVMValueRef noHost = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hostLenAtEnd, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_no_host");
        builder.BuildCondBr(noHost, malformedUrlBlock, buildHostBlock);

        builder.PositionAtEnd(buildHostBlock);
        LLVMValueRef actualHostEnd = builder.BuildSelect(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, builder.BuildLoad2(state.I64, hostEndSlot, "http_host_end_existing"), LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_host_end_is_zero"),
            builder.BuildLoad2(state.I64, indexSlot, "http_host_end_from_index"),
            builder.BuildLoad2(state.I64, hostEndSlot, "http_host_end_final"),
            "http_actual_host_end");
        LLVMValueRef actualHostLen = builder.BuildSub(actualHostEnd, LLVMValueRef.CreateConstInt(state.I64, 7, false), "http_actual_host_len");
        LLVMValueRef hostPtr = builder.BuildGEP2(state.I8, urlBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 7, false) }, "http_host_ptr");
        builder.BuildStore(EmitHeapStringSliceFromBytesPointer(state, hostPtr, actualHostLen, "http_host"), hostSlot);
        LLVMValueRef digitsCount = builder.BuildLoad2(state.I64, portDigitsSlot, "http_digits_count");
        LLVMValueRef hasPortDigits = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, digitsCount, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_has_port_digits");
        var storeParsedPortBlock = state.Function.AppendBasicBlock("http_store_parsed_port");
        builder.BuildCondBr(hasPortDigits, storeParsedPortBlock, havePathBlock);

        builder.PositionAtEnd(storeParsedPortBlock);
        builder.BuildStore(builder.BuildLoad2(state.I64, portValueSlot, "http_port_value_final"), portSlot);
        builder.BuildBr(havePathBlock);

        builder.PositionAtEnd(havePathBlock);
        LLVMValueRef pathIndex = builder.BuildLoad2(state.I64, indexSlot, "http_path_index");
        LLVMValueRef hasExplicitPath = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, pathIndex, urlLen, "http_has_explicit_path");
        var explicitPathBlock = state.Function.AppendBasicBlock("http_explicit_path");
        var defaultPathStoreBlock = state.Function.AppendBasicBlock("http_default_path_store");
        builder.BuildCondBr(hasExplicitPath, explicitPathBlock, defaultPathStoreBlock);

        builder.PositionAtEnd(explicitPathBlock);
        LLVMValueRef explicitPathPtr = builder.BuildGEP2(state.I8, urlBytes, new[] { pathIndex }, "http_explicit_path_ptr");
        LLVMValueRef explicitPathLen = builder.BuildSub(urlLen, pathIndex, "http_explicit_path_len");
        builder.BuildStore(EmitHeapStringSliceFromBytesPointer(state, explicitPathPtr, explicitPathLen, "http_path"), pathSlot);
        builder.BuildBr(connectBlock);

        builder.PositionAtEnd(defaultPathStoreBlock);
        builder.BuildStore(EmitHeapStringLiteral(state, "/"), pathSlot);
        builder.BuildBr(connectBlock);

        builder.PositionAtEnd(connectBlock);
        LLVMValueRef connectResult = EmitTcpConnect(state, builder.BuildLoad2(state.I64, hostSlot, "http_host_value"), builder.BuildLoad2(state.I64, portSlot, "http_port_value"));
        LLVMValueRef connectTag = LoadMemory(state, connectResult, 0, "http_connect_tag");
        LLVMValueRef connectFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, connectTag, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_connect_failed");
        var connectStoreBlock = state.Function.AppendBasicBlock("http_connect_store");
        builder.BuildCondBr(connectFailed, connectStoreBlock, sendBlock);

        builder.PositionAtEnd(connectStoreBlock);
        builder.BuildStore(connectResult, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(sendBlock);
        LLVMValueRef socketValue = LoadMemory(state, connectResult, 8, "http_socket_value");
        builder.BuildStore(socketValue, socketSlot);
        LLVMValueRef requestRef = EmitHttpRequestString(state, builder.BuildLoad2(state.I64, pathSlot, "http_path_value"), builder.BuildLoad2(state.I64, hostSlot, "http_host_header_value"), bodyRef, hasBody);
        LLVMValueRef sendResult = EmitTcpSend(state, socketValue, requestRef);
        LLVMValueRef sendTag = LoadMemory(state, sendResult, 0, "http_send_tag");
        LLVMValueRef sendFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sendTag, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_send_failed");
        var sendErrorBlock = state.Function.AppendBasicBlock("http_send_error");
        builder.BuildCondBr(sendFailed, sendErrorBlock, recvLoopBlock);

        builder.PositionAtEnd(sendErrorBlock);
        EmitTcpClose(state, builder.BuildLoad2(state.I64, socketSlot, "http_send_error_socket"));
        builder.BuildStore(sendResult, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(recvLoopBlock);
        LLVMValueRef recvResult = EmitTcpReceive(state, builder.BuildLoad2(state.I64, socketSlot, "http_recv_socket"), LLVMValueRef.CreateConstInt(state.I64, 65536, false));
        LLVMValueRef recvTag = LoadMemory(state, recvResult, 0, "http_recv_tag");
        LLVMValueRef recvFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, recvTag, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_recv_failed");
        var recvErrorBlock = state.Function.AppendBasicBlock("http_recv_error");
        builder.BuildCondBr(recvFailed, recvErrorBlock, recvInspectBlock);

        builder.PositionAtEnd(recvErrorBlock);
        EmitTcpClose(state, builder.BuildLoad2(state.I64, socketSlot, "http_recv_error_socket"));
        builder.BuildStore(recvResult, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(recvInspectBlock);
        LLVMValueRef chunkRef = LoadMemory(state, recvResult, 8, "http_chunk_ref");
        LLVMValueRef chunkLen = LoadStringLength(state, chunkRef, "http_chunk_len");
        LLVMValueRef chunkEmpty = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, chunkLen, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_chunk_empty");
        var recvAppendBlock = state.Function.AppendBasicBlock("http_recv_append");
        builder.BuildCondBr(chunkEmpty, recvDoneBlock, recvAppendBlock);

        builder.PositionAtEnd(recvAppendBlock);
        LLVMValueRef currentResponse = builder.BuildLoad2(state.I64, responseSlot, "http_current_response");
        LLVMValueRef hasResponse = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, currentResponse, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_has_response");
        var concatResponseBlock = state.Function.AppendBasicBlock("http_concat_response");
        var storeFirstChunkBlock = state.Function.AppendBasicBlock("http_store_first_chunk");
        builder.BuildCondBr(hasResponse, concatResponseBlock, storeFirstChunkBlock);

        builder.PositionAtEnd(storeFirstChunkBlock);
        builder.BuildStore(chunkRef, responseSlot);
        builder.BuildBr(recvLoopBlock);

        builder.PositionAtEnd(concatResponseBlock);
        builder.BuildStore(EmitStringConcat(state, currentResponse, chunkRef), responseSlot);
        builder.BuildBr(recvLoopBlock);

        builder.PositionAtEnd(recvDoneBlock);
        LLVMValueRef closeResult = EmitTcpClose(state, builder.BuildLoad2(state.I64, socketSlot, "http_close_socket"));
        LLVMValueRef closeTag = LoadMemory(state, closeResult, 0, "http_close_tag");
        LLVMValueRef closeFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, closeTag, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_close_failed");
        builder.BuildCondBr(closeFailed, closeErrorBlock, parseResponseBlock);

        builder.PositionAtEnd(parseResponseBlock);
        LLVMValueRef responseRef = builder.BuildLoad2(state.I64, responseSlot, "http_response_value");
        LLVMValueRef emptyResponse = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, responseRef, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_empty_response");
        var ensureEmptyResponseBlock = state.Function.AppendBasicBlock("http_ensure_empty_response");
        var parseResponseContinueBlock = state.Function.AppendBasicBlock("http_parse_response_continue");
        builder.BuildCondBr(emptyResponse, ensureEmptyResponseBlock, parseResponseContinueBlock);

        builder.PositionAtEnd(ensureEmptyResponseBlock);
        builder.BuildStore(EmitHeapStringLiteral(state, string.Empty), responseSlot);
        builder.BuildBr(parseResponseContinueBlock);

        builder.PositionAtEnd(parseResponseContinueBlock);
        LLVMValueRef finalResponse = builder.BuildLoad2(state.I64, responseSlot, "http_final_response");
        LLVMValueRef responseLen = LoadStringLength(state, finalResponse, "http_response_len");
        LLVMValueRef responseTooShort = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, responseLen, LLVMValueRef.CreateConstInt(state.I64, 12, false), "http_response_too_short");
        var parseHeadersBlock = state.Function.AppendBasicBlock("http_parse_headers");
        builder.BuildCondBr(responseTooShort, malformedResponseBlock, parseHeadersBlock);

        builder.PositionAtEnd(parseHeadersBlock);
        LLVMValueRef responseBytes = GetStringBytesPointer(state, finalResponse, "http_response_bytes");
        LLVMValueRef separatorIndex = EmitFindByteSequence(state, responseBytes, responseLen, "\r\n\r\n"u8.ToArray(), "http_separator");
        LLVMValueRef hasSeparator = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, separatorIndex, LLVMValueRef.CreateConstInt(state.I64, 0, true), "http_has_separator");
        var parseStatusBlock = state.Function.AppendBasicBlock("http_parse_status");
        builder.BuildCondBr(hasSeparator, parseStatusBlock, malformedResponseBlock);

        builder.PositionAtEnd(parseStatusBlock);
        LLVMValueRef headerLength = separatorIndex;
        LLVMValueRef statusSpaceIndex = EmitFindByte(state, responseBytes, headerLength, 0, (byte)' ', "http_status_space");
        LLVMValueRef hasStatusSpace = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, statusSpaceIndex, LLVMValueRef.CreateConstInt(state.I64, 0, true), "http_has_status_space");
        var parseDigitsBlock = state.Function.AppendBasicBlock("http_parse_digits");
        builder.BuildCondBr(hasStatusSpace, parseDigitsBlock, malformedResponseBlock);

        builder.PositionAtEnd(parseDigitsBlock);
        LLVMValueRef statusEnd = builder.BuildAdd(statusSpaceIndex, LLVMValueRef.CreateConstInt(state.I64, 3, false), "http_status_end");
        LLVMValueRef digitsInRange = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, statusEnd, headerLength, "http_status_digits_in_range");
        var parseDigitsContinueBlock = state.Function.AppendBasicBlock("http_parse_digits_continue");
        builder.BuildCondBr(digitsInRange, parseDigitsContinueBlock, malformedResponseBlock);

        builder.PositionAtEnd(parseDigitsContinueBlock);
        LLVMValueRef hundredsByte = LoadByteAt(state, responseBytes, builder.BuildAdd(statusSpaceIndex, LLVMValueRef.CreateConstInt(state.I64, 1, false), "http_hundreds_idx"), "http_hundreds_byte");
        LLVMValueRef tensByte = LoadByteAt(state, responseBytes, builder.BuildAdd(statusSpaceIndex, LLVMValueRef.CreateConstInt(state.I64, 2, false), "http_tens_idx"), "http_tens_byte");
        LLVMValueRef onesByte = LoadByteAt(state, responseBytes, builder.BuildAdd(statusSpaceIndex, LLVMValueRef.CreateConstInt(state.I64, 3, false), "http_ones_idx"), "http_ones_byte");
        LLVMValueRef digitsValid = builder.BuildAnd(
            builder.BuildAnd(
                BuildByteRangeCheck(state, builder.BuildZExt(hundredsByte, state.I64, "http_hundreds_i64"), (byte)'0', (byte)'9', "http_hundreds_range"),
                BuildByteRangeCheck(state, builder.BuildZExt(tensByte, state.I64, "http_tens_i64"), (byte)'0', (byte)'9', "http_tens_range"),
                "http_digits_first"),
            BuildByteRangeCheck(state, builder.BuildZExt(onesByte, state.I64, "http_ones_i64"), (byte)'0', (byte)'9', "http_ones_range"),
            "http_digits_valid");
        var detectChunkedBlock = state.Function.AppendBasicBlock("http_detect_chunked");
        builder.BuildCondBr(digitsValid, detectChunkedBlock, malformedResponseBlock);

        builder.PositionAtEnd(detectChunkedBlock);
        LLVMValueRef chunkedHeaderIndex = EmitFindByteSequence(state, responseBytes, headerLength, "Transfer-Encoding: chunked"u8.ToArray(), "http_chunked_header");
        LLVMValueRef hasChunkedHeader = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, chunkedHeaderIndex, LLVMValueRef.CreateConstInt(state.I64, 0, true), "http_has_chunked_header");
        var buildBodyBlock = state.Function.AppendBasicBlock("http_build_body");
        builder.BuildCondBr(hasChunkedHeader, chunkedErrorBlock, buildBodyBlock);

        builder.PositionAtEnd(buildBodyBlock);
        LLVMValueRef statusCode = builder.BuildAdd(
            builder.BuildAdd(
                builder.BuildMul(builder.BuildSub(builder.BuildZExt(hundredsByte, state.I64, "http_hundreds_code"), LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), "http_hundreds_digit"), LLVMValueRef.CreateConstInt(state.I64, 100, false), "http_hundreds_mul"),
                builder.BuildMul(builder.BuildSub(builder.BuildZExt(tensByte, state.I64, "http_tens_code"), LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), "http_tens_digit"), LLVMValueRef.CreateConstInt(state.I64, 10, false), "http_tens_mul"),
                "http_status_prefix_sum"),
            builder.BuildSub(builder.BuildZExt(onesByte, state.I64, "http_ones_code"), LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), "http_ones_digit"),
            "http_status_code");
        LLVMValueRef bodyStart = builder.BuildAdd(separatorIndex, LLVMValueRef.CreateConstInt(state.I64, 4, false), "http_body_start");
        LLVMValueRef bodyLength = builder.BuildSub(responseLen, bodyStart, "http_body_len");
        LLVMValueRef bodyBytes = builder.BuildGEP2(state.I8, responseBytes, new[] { bodyStart }, "http_body_ptr");
        LLVMValueRef bodyString = EmitHeapStringSliceFromBytesPointer(state, bodyBytes, bodyLength, "http_body");
        LLVMValueRef statusOk = builder.BuildAnd(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, statusCode, LLVMValueRef.CreateConstInt(state.I64, 200, false), "http_status_ge_200"),
            builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, statusCode, LLVMValueRef.CreateConstInt(state.I64, 299, false), "http_status_le_299"),
            "http_status_ok");
        var statusOkBlock = state.Function.AppendBasicBlock("http_status_ok_block");
        var statusErrorBlock = state.Function.AppendBasicBlock("http_status_error_block");
        builder.BuildCondBr(statusOk, statusOkBlock, statusErrorBlock);

        builder.PositionAtEnd(statusOkBlock);
        builder.BuildStore(EmitResultOk(state, bodyString), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(statusErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHttpStatusErrorString(state, statusCode, "http_status_error")), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(httpsErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, HttpHttpsNotSupportedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(closeErrorBlock);
        builder.BuildStore(closeResult, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(malformedResponseBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedResponseMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(chunkedErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, HttpUnsupportedTransferEncodingMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "http_result_value");
    }

    private static LLVMValueRef EmitLinuxTcpConnect(LlvmCodegenState state, LLVMValueRef hostRef, LLVMValueRef port)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "tcp_connect_result");
        LLVMValueRef socketSlot = builder.BuildAlloca(state.I64, "tcp_connect_socket");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), socketSlot);
        LLVMValueRef resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "tcp_connect_resolve");
        LLVMValueRef resolveTag = LoadMemory(state, resolveResult, 0, "tcp_connect_resolve_tag");
        LLVMValueRef resolveFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, resolveTag, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_resolve_failed");
        var resolveErrorBlock = state.Function.AppendBasicBlock("tcp_connect_resolve_error");
        var validatePortBlock = state.Function.AppendBasicBlock("tcp_connect_validate_port");
        var openSocketBlock = state.Function.AppendBasicBlock("tcp_connect_open_socket");
        var connectBlock = state.Function.AppendBasicBlock("tcp_connect_connect");
        var connectFailBlock = state.Function.AppendBasicBlock("tcp_connect_fail");
        var connectCloseBlock = state.Function.AppendBasicBlock("tcp_connect_close_socket");
        var continueBlock = state.Function.AppendBasicBlock("tcp_connect_continue");
        builder.BuildCondBr(resolveFailed, resolveErrorBlock, validatePortBlock);

        builder.PositionAtEnd(resolveErrorBlock);
        builder.BuildStore(resolveResult, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(validatePortBlock);
        LLVMValueRef validPort = builder.BuildAnd(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, port, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_port_gt_zero"),
            builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, port, LLVMValueRef.CreateConstInt(state.I64, 65535, false), "tcp_connect_port_le_max"),
            "tcp_connect_port_valid");
        builder.BuildCondBr(validPort, openSocketBlock, connectFailBlock);

        builder.PositionAtEnd(openSocketBlock);
        LLVMValueRef socketValue = EmitSyscall(
            state,
            SyscallSocket,
            LLVMValueRef.CreateConstInt(state.I64, 2, false),
            LLVMValueRef.CreateConstInt(state.I64, 1, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "tcp_connect_socket_call");
        builder.BuildStore(socketValue, socketSlot);
        LLVMValueRef socketFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, socketValue, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_socket_failed");
        builder.BuildCondBr(socketFailed, connectFailBlock, connectBlock);

        builder.PositionAtEnd(connectBlock);
        LLVMTypeRef sockaddrType = LLVMTypeRef.CreateArray(state.I8, 16);
        LLVMValueRef sockaddrStorage = builder.BuildAlloca(sockaddrType, "tcp_connect_sockaddr");
        LLVMValueRef sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_sockaddr_bytes");
        LLVMTypeRef i16 = state.Target.Context.Int16Type;
        LLVMTypeRef i16Ptr = LLVMTypeRef.CreatePointer(i16, 0);
        LLVMValueRef sockaddrI64Ptr = builder.BuildBitCast(sockaddrBytes, state.I64Ptr, "tcp_connect_sockaddr_i64");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), sockaddrI64Ptr);
        LLVMValueRef sockaddrTailPtr = builder.BuildGEP2(state.I8, sockaddrBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 8, false) }, "tcp_connect_sockaddr_tail");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), builder.BuildBitCast(sockaddrTailPtr, state.I64Ptr, "tcp_connect_sockaddr_tail_i64"));
        builder.BuildStore(LLVMValueRef.CreateConstInt(i16, 2, false), builder.BuildBitCast(sockaddrBytes, i16Ptr, "tcp_connect_family_ptr"));
        LLVMValueRef portPtr = builder.BuildGEP2(state.I8, sockaddrBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 2, false) }, "tcp_connect_port_ptr_byte");
        builder.BuildStore(builder.BuildTrunc(EmitByteSwap16(state, port, "tcp_connect_port_network"), i16, "tcp_connect_port_i16"), builder.BuildBitCast(portPtr, i16Ptr, "tcp_connect_port_ptr"));
        LLVMValueRef addrPtr = builder.BuildGEP2(state.I8, sockaddrBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 4, false) }, "tcp_connect_addr_ptr_byte");
        builder.BuildStore(builder.BuildTrunc(LoadMemory(state, resolveResult, 8, "tcp_connect_addr_value"), state.I32, "tcp_connect_addr_i32"), builder.BuildBitCast(addrPtr, state.I32Ptr, "tcp_connect_addr_ptr"));
        LLVMValueRef connectResult = EmitSyscall(
            state,
            SyscallConnect,
            builder.BuildLoad2(state.I64, socketSlot, "tcp_connect_socket_value"),
            builder.BuildPtrToInt(sockaddrBytes, state.I64, "tcp_connect_sockaddr_ptr"),
            LLVMValueRef.CreateConstInt(state.I64, 16, false),
            "tcp_connect_call");
        LLVMValueRef connectFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, connectResult, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_failed_bool");
        var connectSuccessBlock = state.Function.AppendBasicBlock("tcp_connect_success");
        builder.BuildCondBr(connectFailed, connectCloseBlock, connectSuccessBlock);

        builder.PositionAtEnd(connectCloseBlock);
        EmitSyscall(state, SyscallClose, builder.BuildLoad2(state.I64, socketSlot, "tcp_connect_close_socket_value"), LLVMValueRef.CreateConstInt(state.I64, 0, false), LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_close_call");
        builder.BuildBr(connectFailBlock);

        builder.PositionAtEnd(connectFailBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpConnectFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(connectSuccessBlock);
        builder.BuildStore(EmitResultOk(state, builder.BuildLoad2(state.I64, socketSlot, "tcp_connect_success_socket")), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "tcp_connect_result_value");
    }

    private static LLVMValueRef EmitWindowsTcpConnect(LlvmCodegenState state, LLVMValueRef hostRef, LLVMValueRef port)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "tcp_connect_win_result");
        LLVMValueRef socketSlot = builder.BuildAlloca(state.I64, "tcp_connect_win_socket");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), socketSlot);
        LLVMValueRef resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "tcp_connect_win_resolve");
        LLVMValueRef resolveTag = LoadMemory(state, resolveResult, 0, "tcp_connect_win_resolve_tag");
        LLVMValueRef resolveFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, resolveTag, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_win_resolve_failed");
        var resolveErrorBlock = state.Function.AppendBasicBlock("tcp_connect_win_resolve_error");
        var validatePortBlock = state.Function.AppendBasicBlock("tcp_connect_win_validate_port");
        var initWinsockBlock = state.Function.AppendBasicBlock("tcp_connect_win_init_winsock");
        var openSocketBlock = state.Function.AppendBasicBlock("tcp_connect_win_open_socket");
        var connectBlock = state.Function.AppendBasicBlock("tcp_connect_win_connect");
        var connectCloseBlock = state.Function.AppendBasicBlock("tcp_connect_win_close_socket");
        var connectFailBlock = state.Function.AppendBasicBlock("tcp_connect_win_fail");
        var continueBlock = state.Function.AppendBasicBlock("tcp_connect_win_continue");
        builder.BuildCondBr(resolveFailed, resolveErrorBlock, validatePortBlock);

        builder.PositionAtEnd(resolveErrorBlock);
        builder.BuildStore(resolveResult, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(validatePortBlock);
        LLVMValueRef validPort = builder.BuildAnd(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, port, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_win_port_gt_zero"),
            builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, port, LLVMValueRef.CreateConstInt(state.I64, 65535, false), "tcp_connect_win_port_le_max"),
            "tcp_connect_win_port_valid");
        builder.BuildCondBr(validPort, initWinsockBlock, connectFailBlock);

        builder.PositionAtEnd(initWinsockBlock);
        LLVMTypeRef wsadataType = LLVMTypeRef.CreateArray(state.I8, 512);
        LLVMValueRef wsadata = builder.BuildAlloca(wsadataType, "tcp_connect_win_wsadata");
        LLVMValueRef winsockStarted = EmitWindowsWsaStartup(state, GetArrayElementPointer(state, wsadataType, wsadata, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_win_wsadata_ptr"), "tcp_connect_win_wsastartup");
        builder.BuildCondBr(winsockStarted, openSocketBlock, connectFailBlock);

        builder.PositionAtEnd(openSocketBlock);
        LLVMValueRef socketValue = EmitWindowsSocket(state, 2, 1, 6, "tcp_connect_win_socket_call");
        builder.BuildStore(socketValue, socketSlot);
        LLVMValueRef socketFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, socketValue, LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), "tcp_connect_win_socket_failed");
        builder.BuildCondBr(socketFailed, connectFailBlock, connectBlock);

        builder.PositionAtEnd(connectBlock);
        LLVMTypeRef sockaddrType = LLVMTypeRef.CreateArray(state.I8, 16);
        LLVMValueRef sockaddrStorage = builder.BuildAlloca(sockaddrType, "tcp_connect_win_sockaddr");
        LLVMValueRef sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_win_sockaddr_bytes");
        LLVMTypeRef i16 = state.Target.Context.Int16Type;
        LLVMTypeRef i16Ptr = LLVMTypeRef.CreatePointer(i16, 0);
        LLVMValueRef sockaddrI64Ptr = builder.BuildBitCast(sockaddrBytes, state.I64Ptr, "tcp_connect_win_sockaddr_i64");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), sockaddrI64Ptr);
        LLVMValueRef sockaddrTailPtr = builder.BuildGEP2(state.I8, sockaddrBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 8, false) }, "tcp_connect_win_sockaddr_tail");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), builder.BuildBitCast(sockaddrTailPtr, state.I64Ptr, "tcp_connect_win_sockaddr_tail_i64"));
        builder.BuildStore(LLVMValueRef.CreateConstInt(i16, 2, false), builder.BuildBitCast(sockaddrBytes, i16Ptr, "tcp_connect_win_family_ptr"));
        LLVMValueRef portPtr = builder.BuildGEP2(state.I8, sockaddrBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 2, false) }, "tcp_connect_win_port_ptr_byte");
        builder.BuildStore(builder.BuildTrunc(EmitByteSwap16(state, port, "tcp_connect_win_port_network"), i16, "tcp_connect_win_port_i16"), builder.BuildBitCast(portPtr, i16Ptr, "tcp_connect_win_port_ptr"));
        LLVMValueRef addrPtr = builder.BuildGEP2(state.I8, sockaddrBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 4, false) }, "tcp_connect_win_addr_ptr_byte");
        builder.BuildStore(builder.BuildTrunc(LoadMemory(state, resolveResult, 8, "tcp_connect_win_addr_value"), state.I32, "tcp_connect_win_addr_i32"), builder.BuildBitCast(addrPtr, state.I32Ptr, "tcp_connect_win_addr_ptr"));
        LLVMValueRef connectResult = EmitWindowsConnect(state, builder.BuildLoad2(state.I64, socketSlot, "tcp_connect_win_socket_value"), sockaddrBytes, "tcp_connect_win_connect_call");
        var connectSuccessBlock = state.Function.AppendBasicBlock("tcp_connect_win_success");
        builder.BuildCondBr(connectResult, connectSuccessBlock, connectCloseBlock);

        builder.PositionAtEnd(connectCloseBlock);
        EmitWindowsCloseSocket(state, builder.BuildLoad2(state.I64, socketSlot, "tcp_connect_win_close_socket_value"), "tcp_connect_win_close_socket_call");
        builder.BuildBr(connectFailBlock);

        builder.PositionAtEnd(connectFailBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpConnectFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(connectSuccessBlock);
        builder.BuildStore(EmitResultOk(state, builder.BuildLoad2(state.I64, socketSlot, "tcp_connect_win_success_socket")), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "tcp_connect_win_result_value");
    }

    private static LLVMValueRef EmitLinuxTcpSend(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef textRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "tcp_send_result");
        LLVMValueRef remainingSlot = builder.BuildAlloca(state.I64, "tcp_send_remaining");
        LLVMValueRef cursorSlot = builder.BuildAlloca(state.I64, "tcp_send_cursor");
        LLVMValueRef totalLen = LoadStringLength(state, textRef, "tcp_send_total_len");
        builder.BuildStore(totalLen, remainingSlot);
        builder.BuildStore(GetStringBytesAddress(state, textRef, "tcp_send_cursor_start"), cursorSlot);
        var loopCheckBlock = state.Function.AppendBasicBlock("tcp_send_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock("tcp_send_loop_body");
        var updateBlock = state.Function.AppendBasicBlock("tcp_send_update");
        var failBlock = state.Function.AppendBasicBlock("tcp_send_fail");
        var continueBlock = state.Function.AppendBasicBlock("tcp_send_continue");
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef remaining = builder.BuildLoad2(state.I64, remainingSlot, "tcp_send_remaining_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, remaining, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_send_done");
        var doneBlock = state.Function.AppendBasicBlock("tcp_send_done_block");
        builder.BuildCondBr(done, doneBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef sent = EmitSyscall(state, SyscallWrite, socket, builder.BuildLoad2(state.I64, cursorSlot, "tcp_send_cursor_value"), remaining, "tcp_send_syscall");
        LLVMValueRef sendFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, sent, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_send_failed");
        builder.BuildCondBr(sendFailed, failBlock, updateBlock);

        builder.PositionAtEnd(updateBlock);
        LLVMValueRef cursor = builder.BuildLoad2(state.I64, cursorSlot, "tcp_send_cursor_current");
        builder.BuildStore(builder.BuildSub(remaining, sent, "tcp_send_remaining_next"), remainingSlot);
        builder.BuildStore(builder.BuildAdd(cursor, sent, "tcp_send_cursor_next"), cursorSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(doneBlock);
        builder.BuildStore(EmitResultOk(state, totalLen), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(failBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "tcp_send_result_value");
    }

    private static LLVMValueRef EmitWindowsTcpSend(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef textRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "tcp_send_win_result");
        LLVMValueRef remainingSlot = builder.BuildAlloca(state.I64, "tcp_send_win_remaining");
        LLVMValueRef cursorSlot = builder.BuildAlloca(state.I64, "tcp_send_win_cursor");
        LLVMValueRef totalLen = LoadStringLength(state, textRef, "tcp_send_win_total_len");
        builder.BuildStore(totalLen, remainingSlot);
        builder.BuildStore(GetStringBytesAddress(state, textRef, "tcp_send_win_cursor_start"), cursorSlot);
        var loopCheckBlock = state.Function.AppendBasicBlock("tcp_send_win_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock("tcp_send_win_loop_body");
        var updateBlock = state.Function.AppendBasicBlock("tcp_send_win_update");
        var failBlock = state.Function.AppendBasicBlock("tcp_send_win_fail");
        var continueBlock = state.Function.AppendBasicBlock("tcp_send_win_continue");
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef remaining = builder.BuildLoad2(state.I64, remainingSlot, "tcp_send_win_remaining_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, remaining, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_send_win_done");
        var doneBlock = state.Function.AppendBasicBlock("tcp_send_win_done_block");
        builder.BuildCondBr(done, doneBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef chunk = builder.BuildSelect(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, remaining, LLVMValueRef.CreateConstInt(state.I64, int.MaxValue, false), "tcp_send_win_chunk_gt"),
            LLVMValueRef.CreateConstInt(state.I64, int.MaxValue, false),
            remaining,
            "tcp_send_win_chunk");
        LLVMValueRef sentRaw = EmitWindowsSend(state, socket, builder.BuildIntToPtr(builder.BuildLoad2(state.I64, cursorSlot, "tcp_send_win_cursor_value"), state.I8Ptr, "tcp_send_win_cursor_ptr"), builder.BuildTrunc(chunk, state.I32, "tcp_send_win_chunk_i32"), "tcp_send_win_call");
        LLVMValueRef sendFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, sentRaw, LLVMValueRef.CreateConstInt(state.I32, 0, true), "tcp_send_win_failed");
        builder.BuildCondBr(sendFailed, failBlock, updateBlock);

        builder.PositionAtEnd(updateBlock);
        LLVMValueRef sent = builder.BuildSExt(sentRaw, state.I64, "tcp_send_win_sent");
        LLVMValueRef cursor = builder.BuildLoad2(state.I64, cursorSlot, "tcp_send_win_cursor_current");
        builder.BuildStore(builder.BuildSub(remaining, sent, "tcp_send_win_remaining_next"), remainingSlot);
        builder.BuildStore(builder.BuildAdd(cursor, sent, "tcp_send_win_cursor_next"), cursorSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(doneBlock);
        builder.BuildStore(EmitResultOk(state, totalLen), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(failBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "tcp_send_win_result_value");
    }

    private static LLVMValueRef EmitLinuxTcpReceive(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef maxBytes)
    {
        return EmitTcpReceiveCommon(state, socket, maxBytes, "tcp_receive", static (s, sock, bytesPtr, max, name) => EmitSyscall(s, SyscallRead, sock, s.Target.Builder.BuildPtrToInt(bytesPtr, s.I64, name + "_ptr"), s.Target.Builder.BuildSExt(max, s.I64, name + "_len"), name));
    }

    private static LLVMValueRef EmitWindowsTcpReceive(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef maxBytes)
    {
        return EmitTcpReceiveCommon(state, socket, maxBytes, "tcp_receive_win", static (s, sock, bytesPtr, max, name) => s.Target.Builder.BuildSExt(EmitWindowsRecv(s, sock, bytesPtr, max, name), s.I64, name + "_sext"));
    }

    private static LLVMValueRef EmitTcpReceiveCommon(
        LlvmCodegenState state,
        LLVMValueRef socket,
        LLVMValueRef maxBytes,
        string prefix,
        Func<LlvmCodegenState, LLVMValueRef, LLVMValueRef, LLVMValueRef, string, LLVMValueRef> emitRead)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, prefix + "_result");
        var invalidMaxBlock = state.Function.AppendBasicBlock(prefix + "_invalid_max");
        var readBlock = state.Function.AppendBasicBlock(prefix + "_read");
        var handleReadBlock = state.Function.AppendBasicBlock(prefix + "_handle_read");
        var invalidUtf8Block = state.Function.AppendBasicBlock(prefix + "_invalid_utf8");
        var failBlock = state.Function.AppendBasicBlock(prefix + "_fail");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");
        LLVMValueRef positiveMax = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, maxBytes, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_positive_max");
        builder.BuildCondBr(positiveMax, readBlock, invalidMaxBlock);

        builder.PositionAtEnd(invalidMaxBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidMaxBytesMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(readBlock);
        LLVMValueRef stringRef = EmitAllocDynamic(state, builder.BuildAdd(maxBytes, LLVMValueRef.CreateConstInt(state.I64, 8, false), prefix + "_size"));
        StoreMemory(state, stringRef, 0, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_len_init");
        LLVMValueRef readCount = emitRead(state, socket, GetStringBytesPointer(state, stringRef, prefix + "_bytes"), builder.BuildTrunc(maxBytes, state.I32, prefix + "_max_i32"), prefix + "_read_call");
        LLVMValueRef readFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, readCount, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_read_failed");
        builder.BuildCondBr(readFailed, failBlock, handleReadBlock);

        builder.PositionAtEnd(handleReadBlock);
        StoreMemory(state, stringRef, 0, readCount, prefix + "_len_store");
        LLVMValueRef isEmpty = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, readCount, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_is_empty");
        var successBlock = state.Function.AppendBasicBlock(prefix + "_success");
        var validateBlock = state.Function.AppendBasicBlock(prefix + "_validate");
        builder.BuildCondBr(isEmpty, successBlock, validateBlock);

        builder.PositionAtEnd(validateBlock);
        LLVMValueRef utf8Valid = EmitValidateUtf8(state, GetStringBytesPointer(state, stringRef, prefix + "_validate_bytes"), readCount, prefix + "_utf8");
        LLVMValueRef valid = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, utf8Valid, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_utf8_valid");
        builder.BuildCondBr(valid, successBlock, invalidUtf8Block);

        builder.PositionAtEnd(invalidUtf8Block);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidUtf8Message)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(successBlock);
        builder.BuildStore(EmitResultOk(state, stringRef), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(failBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpReceiveFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, prefix + "_result_value");
    }

    private static LLVMValueRef EmitLinuxTcpClose(LlvmCodegenState state, LLVMValueRef socket)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef result = EmitSyscall(state, SyscallClose, socket, LLVMValueRef.CreateConstInt(state.I64, 0, false), LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_close_call");
        LLVMValueRef success = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, result, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_close_success");
        return builder.BuildSelect(success, EmitResultOk(state, EmitUnitValue(state)), EmitResultError(state, EmitHeapStringLiteral(state, TcpCloseFailedMessage)), "tcp_close_result");
    }

    private static LLVMValueRef EmitWindowsTcpClose(LlvmCodegenState state, LLVMValueRef socket)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef closeResult = EmitWindowsCloseSocket(state, socket, "tcp_close_win_call");
        LLVMValueRef success = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, closeResult, LLVMValueRef.CreateConstInt(state.I32, 0, false), "tcp_close_win_success");
        return builder.BuildSelect(success, EmitResultOk(state, EmitUnitValue(state)), EmitResultError(state, EmitHeapStringLiteral(state, TcpCloseFailedMessage)), "tcp_close_win_result");
    }

    private static LLVMValueRef EmitResolveHostIpv4OrLocalhost(LlvmCodegenState state, LLVMValueRef hostRef, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, prefix + "_result");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_index");
        LLVMValueRef partSlot = builder.BuildAlloca(state.I64, prefix + "_part");
        LLVMValueRef currentSlot = builder.BuildAlloca(state.I64, prefix + "_current");
        LLVMValueRef seenDigitSlot = builder.BuildAlloca(state.I64, prefix + "_seen_digit");
        LLVMValueRef addressSlot = builder.BuildAlloca(state.I64, prefix + "_address");
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpResolveFailedMessage)), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), partSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), currentSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), seenDigitSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), addressSlot);

        LLVMValueRef localhostEquals = EmitStringComparison(state, hostRef, EmitStackStringObject(state, "localhost"));
        LLVMValueRef isLocalhost = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, localhostEquals, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_is_localhost");
        LLVMValueRef hostLen = LoadStringLength(state, hostRef, prefix + "_host_len");
        LLVMValueRef hostBytes = GetStringBytesPointer(state, hostRef, prefix + "_host_bytes");
        var localhostBlock = state.Function.AppendBasicBlock(prefix + "_localhost");
        var parseLoopBlock = state.Function.AppendBasicBlock(prefix + "_parse_loop");
        var parseInspectBlock = state.Function.AppendBasicBlock(prefix + "_parse_inspect");
        var digitBlock = state.Function.AppendBasicBlock(prefix + "_digit");
        var dotBlock = state.Function.AppendBasicBlock(prefix + "_dot");
        var failBlock = state.Function.AppendBasicBlock(prefix + "_fail");
        var finalizeBlock = state.Function.AppendBasicBlock(prefix + "_finalize");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");
        builder.BuildCondBr(isLocalhost, localhostBlock, parseLoopBlock);

        builder.PositionAtEnd(localhostBlock);
        builder.BuildStore(EmitResultOk(state, LLVMValueRef.CreateConstInt(state.I64, 0x0100007FUL, false)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(parseLoopBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, index, hostLen, prefix + "_done");
        builder.BuildCondBr(done, finalizeBlock, parseInspectBlock);

        builder.PositionAtEnd(parseInspectBlock);
        LLVMValueRef currentByte = LoadByteAt(state, hostBytes, index, prefix + "_current_byte");
        LLVMValueRef currentByte64 = builder.BuildZExt(currentByte, state.I64, prefix + "_current_byte_i64");
        LLVMValueRef isDigit = BuildByteRangeCheck(state, currentByte64, (byte)'0', (byte)'9', prefix + "_digit_range");
        var dotCheckBlock = state.Function.AppendBasicBlock(prefix + "_dot_check");
        builder.BuildCondBr(isDigit, digitBlock, dotCheckBlock);

        builder.PositionAtEnd(dotCheckBlock);
        LLVMValueRef isDot = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, currentByte, LLVMValueRef.CreateConstInt(state.I8, (byte)'.', false), prefix + "_is_dot");
        builder.BuildCondBr(isDot, dotBlock, failBlock);

        builder.PositionAtEnd(digitBlock);
        LLVMValueRef currentValue = builder.BuildLoad2(state.I64, currentSlot, prefix + "_current_value");
        LLVMValueRef parsedDigit = builder.BuildSub(currentByte64, LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), prefix + "_parsed_digit");
        LLVMValueRef nextValue = builder.BuildAdd(builder.BuildMul(currentValue, LLVMValueRef.CreateConstInt(state.I64, 10, false), prefix + "_mul"), parsedDigit, prefix + "_next_value");
        LLVMValueRef valueTooLarge = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, nextValue, LLVMValueRef.CreateConstInt(state.I64, 255, false), prefix + "_value_too_large");
        var storeDigitBlock = state.Function.AppendBasicBlock(prefix + "_store_digit");
        builder.BuildCondBr(valueTooLarge, failBlock, storeDigitBlock);

        builder.PositionAtEnd(storeDigitBlock);
        builder.BuildStore(nextValue, currentSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 1, false), seenDigitSlot);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_index_next"), indexSlot);
        builder.BuildBr(parseLoopBlock);

        builder.PositionAtEnd(dotBlock);
        LLVMValueRef seenDigit = builder.BuildLoad2(state.I64, seenDigitSlot, prefix + "_seen_digit_value");
        LLVMValueRef part = builder.BuildLoad2(state.I64, partSlot, prefix + "_part_value");
        LLVMValueRef dotValid = builder.BuildAnd(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, seenDigit, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_dot_seen_digit"),
            builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, part, LLVMValueRef.CreateConstInt(state.I64, 3, false), prefix + "_dot_part_lt_three"),
            prefix + "_dot_valid");
        var storeDotBlock = state.Function.AppendBasicBlock(prefix + "_store_dot");
        builder.BuildCondBr(dotValid, storeDotBlock, failBlock);

        builder.PositionAtEnd(storeDotBlock);
        LLVMValueRef addressValue = builder.BuildLoad2(state.I64, addressSlot, prefix + "_address_value");
        LLVMValueRef shiftedOctet = builder.BuildShl(builder.BuildLoad2(state.I64, currentSlot, prefix + "_octet_value"), builder.BuildMul(part, LLVMValueRef.CreateConstInt(state.I64, 8, false), prefix + "_octet_shift"), prefix + "_shifted_octet");
        builder.BuildStore(builder.BuildOr(addressValue, shiftedOctet, prefix + "_address_next"), addressSlot);
        builder.BuildStore(builder.BuildAdd(part, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_part_next"), partSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), currentSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), seenDigitSlot);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_index_after_dot"), indexSlot);
        builder.BuildBr(parseLoopBlock);

        builder.PositionAtEnd(finalizeBlock);
        LLVMValueRef finalSeenDigit = builder.BuildLoad2(state.I64, seenDigitSlot, prefix + "_final_seen_digit");
        LLVMValueRef finalPart = builder.BuildLoad2(state.I64, partSlot, prefix + "_final_part");
        LLVMValueRef finalValid = builder.BuildAnd(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, finalSeenDigit, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_final_seen_digit_ok"),
            builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, finalPart, LLVMValueRef.CreateConstInt(state.I64, 3, false), prefix + "_final_part_eq_three"),
            prefix + "_final_valid");
        var storeFinalBlock = state.Function.AppendBasicBlock(prefix + "_store_final");
        builder.BuildCondBr(finalValid, storeFinalBlock, failBlock);

        builder.PositionAtEnd(storeFinalBlock);
        LLVMValueRef finalAddress = builder.BuildOr(
            builder.BuildLoad2(state.I64, addressSlot, prefix + "_address_before_final"),
            builder.BuildShl(builder.BuildLoad2(state.I64, currentSlot, prefix + "_current_before_final"), LLVMValueRef.CreateConstInt(state.I64, 24, false), prefix + "_final_shifted_octet"),
            prefix + "_final_address");
        builder.BuildStore(EmitResultOk(state, finalAddress), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(failBlock);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, prefix + "_result_value");
    }

    private static LLVMValueRef EmitHttpRequestString(LlvmCodegenState state, LLVMValueRef pathRef, LLVMValueRef hostRef, LLVMValueRef bodyRef, bool hasBody)
    {
        LLVMValueRef request = EmitHeapStringLiteral(state, hasBody ? "POST " : "GET ");
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

    private static LLVMValueRef EmitHttpStatusErrorString(LlvmCodegenState state, LLVMValueRef statusCode, string prefix)
    {
        return EmitStringConcat(state, EmitHeapStringLiteral(state, "HTTP "), EmitNonNegativeIntToString(state, statusCode, prefix + "_code"));
    }

    private static LLVMValueRef EmitNonNegativeIntToString(LlvmCodegenState state, LLVMValueRef value, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef bufferType = LLVMTypeRef.CreateArray(state.I8, 32);
        LLVMValueRef buffer = builder.BuildAlloca(bufferType, prefix + "_buffer");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_index");
        LLVMValueRef workSlot = builder.BuildAlloca(state.I64, prefix + "_work");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);
        builder.BuildStore(value, workSlot);

        var zeroBlock = state.Function.AppendBasicBlock(prefix + "_zero");
        var loopCheckBlock = state.Function.AppendBasicBlock(prefix + "_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock(prefix + "_loop_body");
        var finishBlock = state.Function.AppendBasicBlock(prefix + "_finish");
        LLVMValueRef isZero = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, value, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_is_zero");
        builder.BuildCondBr(isZero, zeroBlock, loopCheckBlock);

        builder.PositionAtEnd(zeroBlock);
        StoreBufferByte(state, buffer, LLVMValueRef.CreateConstInt(state.I64, 31, false), (byte)'0');
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 1, false), indexSlot);
        builder.BuildBr(finishBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef work = builder.BuildLoad2(state.I64, workSlot, prefix + "_work_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, work, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_done");
        builder.BuildCondBr(done, finishBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef digit = builder.BuildURem(work, LLVMValueRef.CreateConstInt(state.I64, 10, false), prefix + "_digit");
        builder.BuildStore(builder.BuildUDiv(work, LLVMValueRef.CreateConstInt(state.I64, 10, false), prefix + "_next_work"), workSlot);
        LLVMValueRef idx = builder.BuildLoad2(state.I64, indexSlot, prefix + "_idx_value");
        StoreBufferByte(state, buffer, builder.BuildSub(LLVMValueRef.CreateConstInt(state.I64, 31, false), idx, prefix + "_write_idx"), builder.BuildAdd(digit, LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), prefix + "_ascii"));
        builder.BuildStore(builder.BuildAdd(idx, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_idx_next"), indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(finishBlock);
        LLVMValueRef count = builder.BuildLoad2(state.I64, indexSlot, prefix + "_count");
        LLVMValueRef startIndex = builder.BuildSub(LLVMValueRef.CreateConstInt(state.I64, 32, false), count, prefix + "_start_index");
        LLVMValueRef startPtr = GetArrayElementPointer(state, bufferType, buffer, startIndex, prefix + "_start_ptr");
        return EmitHeapStringSliceFromBytesPointer(state, startPtr, count, prefix + "_string");
    }

    private static LLVMValueRef EmitHeapStringSliceFromBytesPointer(LlvmCodegenState state, LLVMValueRef bytesPtr, LLVMValueRef len, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef stringRef = EmitAllocDynamic(state, builder.BuildAdd(len, LLVMValueRef.CreateConstInt(state.I64, 8, false), prefix + "_size"));
        StoreMemory(state, stringRef, 0, len, prefix + "_len");
        EmitCopyBytes(state, GetStringBytesPointer(state, stringRef, prefix + "_dest"), bytesPtr, len, prefix + "_copy");
        return stringRef;
    }

    private static LLVMValueRef EmitStartsWith(LlvmCodegenState state, LLVMValueRef sourceRef, LLVMValueRef prefixRef, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef sourceLen = LoadStringLength(state, sourceRef, prefix + "_source_len");
        LLVMValueRef prefixLen = LoadStringLength(state, prefixRef, prefix + "_prefix_len");
        LLVMValueRef enough = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, sourceLen, prefixLen, prefix + "_enough");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, prefix + "_result");
        var compareBlock = state.Function.AppendBasicBlock(prefix + "_compare");
        var falseBlock = state.Function.AppendBasicBlock(prefix + "_false");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");
        builder.BuildCondBr(enough, compareBlock, falseBlock);

        builder.PositionAtEnd(falseBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(compareBlock);
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_index");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);
        LLVMValueRef sourceBytes = GetStringBytesPointer(state, sourceRef, prefix + "_source_bytes");
        LLVMValueRef prefixBytes = GetStringBytesPointer(state, prefixRef, prefix + "_prefix_bytes");
        var loopCheckBlock = state.Function.AppendBasicBlock(prefix + "_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock(prefix + "_loop_body");
        var successBlock = state.Function.AppendBasicBlock(prefix + "_success");
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, index, prefixLen, prefix + "_done");
        builder.BuildCondBr(done, successBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef sourceByte = LoadByteAt(state, sourceBytes, index, prefix + "_source_byte");
        LLVMValueRef prefixByte = LoadByteAt(state, prefixBytes, index, prefix + "_prefix_byte");
        LLVMValueRef matches = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, sourceByte, prefixByte, prefix + "_matches");
        var advanceBlock = state.Function.AppendBasicBlock(prefix + "_advance");
        builder.BuildCondBr(matches, advanceBlock, falseBlock);

        builder.PositionAtEnd(advanceBlock);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_index_next"), indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(successBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 1, false), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, prefix + "_result_value");
    }

    private static LLVMValueRef EmitFindByte(LlvmCodegenState state, LLVMValueRef bytesPtr, LLVMValueRef len, int startOffset, byte targetByte, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, prefix + "_result");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_index");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, (ulong)startOffset, false), indexSlot);
        var loopCheckBlock = state.Function.AppendBasicBlock(prefix + "_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock(prefix + "_loop_body");
        var foundBlock = state.Function.AppendBasicBlock(prefix + "_found");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, index, len, prefix + "_done");
        builder.BuildCondBr(done, continueBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef currentByte = LoadByteAt(state, bytesPtr, index, prefix + "_byte");
        LLVMValueRef matches = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, currentByte, LLVMValueRef.CreateConstInt(state.I8, targetByte, false), prefix + "_matches");
        var advanceBlock = state.Function.AppendBasicBlock(prefix + "_advance");
        builder.BuildCondBr(matches, foundBlock, advanceBlock);

        builder.PositionAtEnd(foundBlock);
        builder.BuildStore(index, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(advanceBlock);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_index_next"), indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, prefix + "_result_value");
    }

    private static LLVMValueRef EmitFindByteSequence(LlvmCodegenState state, LLVMValueRef bytesPtr, LLVMValueRef len, IReadOnlyList<byte> patternBytes, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, prefix + "_result");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_index");
        LLVMValueRef patternLen = LLVMValueRef.CreateConstInt(state.I64, (ulong)patternBytes.Count, false);
        LLVMValueRef patternPtr = EmitStackByteArray(state, patternBytes);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);
        var loopCheckBlock = state.Function.AppendBasicBlock(prefix + "_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock(prefix + "_loop_body");
        var compareLoopBlock = state.Function.AppendBasicBlock(prefix + "_compare_loop");
        var foundBlock = state.Function.AppendBasicBlock(prefix + "_found");
        var advanceBlock = state.Function.AppendBasicBlock(prefix + "_advance");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");
        LLVMValueRef compareIndexSlot = builder.BuildAlloca(state.I64, prefix + "_compare_index");
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index_value");
        LLVMValueRef canMatch = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, builder.BuildAdd(index, patternLen, prefix + "_candidate_end"), len, prefix + "_can_match");
        builder.BuildCondBr(canMatch, loopBodyBlock, continueBlock);

        builder.PositionAtEnd(loopBodyBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), compareIndexSlot);
        builder.BuildBr(compareLoopBlock);

        builder.PositionAtEnd(compareLoopBlock);
        LLVMValueRef compareIndex = builder.BuildLoad2(state.I64, compareIndexSlot, prefix + "_compare_index_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, compareIndex, patternLen, prefix + "_compare_done");
        var compareBodyBlock = state.Function.AppendBasicBlock(prefix + "_compare_body");
        builder.BuildCondBr(done, foundBlock, compareBodyBlock);

        builder.PositionAtEnd(compareBodyBlock);
        LLVMValueRef actualByte = LoadByteAt(state, bytesPtr, builder.BuildAdd(index, compareIndex, prefix + "_actual_index"), prefix + "_actual_byte");
        LLVMValueRef expectedByte = LoadByteAt(state, patternPtr, compareIndex, prefix + "_expected_byte");
        LLVMValueRef matches = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, actualByte, expectedByte, prefix + "_compare_matches");
        var compareAdvanceBlock = state.Function.AppendBasicBlock(prefix + "_compare_advance");
        builder.BuildCondBr(matches, compareAdvanceBlock, advanceBlock);

        builder.PositionAtEnd(compareAdvanceBlock);
        builder.BuildStore(builder.BuildAdd(compareIndex, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_compare_index_next"), compareIndexSlot);
        builder.BuildBr(compareLoopBlock);

        builder.PositionAtEnd(foundBlock);
        builder.BuildStore(index, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(advanceBlock);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_index_next"), indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, prefix + "_result_value");
    }

    private static LLVMValueRef EmitByteSwap16(LlvmCodegenState state, LLVMValueRef value, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef maskedLow = builder.BuildAnd(value, LLVMValueRef.CreateConstInt(state.I64, 0xFF, false), prefix + "_low");
        LLVMValueRef maskedHigh = builder.BuildAnd(builder.BuildLShr(value, LLVMValueRef.CreateConstInt(state.I64, 8, false), prefix + "_shr"), LLVMValueRef.CreateConstInt(state.I64, 0xFF, false), prefix + "_high");
        return builder.BuildOr(builder.BuildShl(maskedLow, LLVMValueRef.CreateConstInt(state.I64, 8, false), prefix + "_low_shifted"), maskedHigh, prefix + "_result");
    }

    private static LLVMValueRef EmitWindowsWsaStartup(LlvmCodegenState state, LLVMValueRef wsadataPtr, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef i16 = state.Target.Context.Int16Type;
        LLVMTypeRef wsaStartupType = LLVMTypeRef.CreateFunction(state.I32, [i16, state.I8Ptr]);
        LLVMValueRef wsaStartupPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(wsaStartupType, 0),
            state.WindowsWsaStartupImport,
            name + "_ptr");
        LLVMValueRef result = builder.BuildCall2(
            wsaStartupType,
            wsaStartupPtr,
            new[]
            {
                LLVMValueRef.CreateConstInt(i16, 0x0202, false),
                wsadataPtr
            },
            name);
        return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result, LLVMValueRef.CreateConstInt(state.I32, 0, false), name + "_success");
    }

    private static LLVMValueRef EmitWindowsSocket(LlvmCodegenState state, int af, int socketTypeValue, int protocol, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef socketType = LLVMTypeRef.CreateFunction(state.I64, [state.I32, state.I32, state.I32]);
        LLVMValueRef socketPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(socketType, 0),
            state.WindowsSocketImport,
            name + "_ptr");
        return builder.BuildCall2(
            socketType,
            socketPtr,
            new[]
            {
                LLVMValueRef.CreateConstInt(state.I32, (uint)af, false),
                LLVMValueRef.CreateConstInt(state.I32, (uint)socketTypeValue, false),
                LLVMValueRef.CreateConstInt(state.I32, (uint)protocol, false)
            },
            name);
    }

    private static LLVMValueRef EmitWindowsConnect(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef sockaddrPtr, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef connectType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32]);
        LLVMValueRef connectPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(connectType, 0),
            state.WindowsConnectImport,
            name + "_ptr");
        LLVMValueRef result = builder.BuildCall2(
            connectType,
            connectPtr,
            new[]
            {
                socket,
                sockaddrPtr,
                LLVMValueRef.CreateConstInt(state.I32, 16, false)
            },
            name);
        return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result, LLVMValueRef.CreateConstInt(state.I32, 0, false), name + "_success");
    }

    private static LLVMValueRef EmitWindowsSend(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef buffer, LLVMValueRef len, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef sendType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32]);
        LLVMValueRef sendPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(sendType, 0),
            state.WindowsSendImport,
            name + "_ptr");
        return builder.BuildCall2(
            sendType,
            sendPtr,
            new[]
            {
                socket,
                buffer,
                len,
                LLVMValueRef.CreateConstInt(state.I32, 0, false)
            },
            name);
    }

    private static LLVMValueRef EmitWindowsRecv(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef buffer, LLVMValueRef len, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef recvType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32]);
        LLVMValueRef recvPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(recvType, 0),
            state.WindowsRecvImport,
            name + "_ptr");
        return builder.BuildCall2(
            recvType,
            recvPtr,
            new[]
            {
                socket,
                buffer,
                len,
                LLVMValueRef.CreateConstInt(state.I32, 0, false)
            },
            name);
    }

    private static LLVMValueRef EmitWindowsCloseSocket(LlvmCodegenState state, LLVMValueRef socket, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef closeSocketType = LLVMTypeRef.CreateFunction(state.I32, [state.I64]);
        LLVMValueRef closeSocketPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(closeSocketType, 0),
            state.WindowsCloseSocketImport,
            name + "_ptr");
        return builder.BuildCall2(
            closeSocketType,
            closeSocketPtr,
            new[] { socket },
            name);
    }

    private static LLVMValueRef EmitWindowsCreateFile(LlvmCodegenState state, LLVMValueRef pathCstr, int desiredAccess, int shareMode, int creationDisposition, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef createFileType = LLVMTypeRef.CreateFunction(state.I64, [state.I8Ptr, state.I32, state.I32, state.I8Ptr, state.I32, state.I32, state.I64]);
        LLVMValueRef createFilePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(createFileType, 0),
            state.WindowsCreateFileImport,
            name + "_ptr");
        return builder.BuildCall2(
            createFileType,
            createFilePtr,
            new[]
            {
                pathCstr,
                LLVMValueRef.CreateConstInt(state.I32, unchecked((uint)desiredAccess), true),
                LLVMValueRef.CreateConstInt(state.I32, unchecked((uint)shareMode), false),
                builder.BuildIntToPtr(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.I8Ptr, name + "_security"),
                LLVMValueRef.CreateConstInt(state.I32, unchecked((uint)creationDisposition), false),
                LLVMValueRef.CreateConstInt(state.I32, 0x80, false),
                LLVMValueRef.CreateConstInt(state.I64, 0, false)
            },
            name);
    }

    private static void EmitWindowsCloseHandle(LlvmCodegenState state, LLVMValueRef handle, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef closeHandleType = LLVMTypeRef.CreateFunction(state.I32, [state.I64]);
        LLVMValueRef closeHandlePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(closeHandleType, 0),
            state.WindowsCloseHandleImport,
            name + "_ptr");
        builder.BuildCall2(
            closeHandleType,
            closeHandlePtr,
            new[] { handle },
            name);
    }

    private static LLVMValueRef EmitWindowsGetFileAttributes(LlvmCodegenState state, LLVMValueRef pathCstr, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef getFileAttributesType = LLVMTypeRef.CreateFunction(state.I32, [state.I8Ptr]);
        LLVMValueRef getFileAttributesPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(getFileAttributesType, 0),
            state.WindowsGetFileAttributesImport,
            name + "_ptr");
        return builder.BuildCall2(
            getFileAttributesType,
            getFileAttributesPtr,
            new[] { pathCstr },
            name);
    }

    private static LLVMValueRef EmitWindowsReadFile(LlvmCodegenState state, LLVMValueRef handle, LLVMValueRef buffer, LLVMValueRef len, LLVMValueRef bytesReadSlot, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef readFileType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I32, 0, false), bytesReadSlot);
        LLVMValueRef readFilePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(readFileType, 0),
            state.WindowsReadFileImport,
            name + "_ptr");
        LLVMValueRef callResult = builder.BuildCall2(
            readFileType,
            readFilePtr,
            new[]
            {
                handle,
                buffer,
                len,
                bytesReadSlot,
                builder.BuildIntToPtr(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.I8Ptr, name + "_overlapped")
            },
            name);
        return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, callResult, LLVMValueRef.CreateConstInt(state.I32, 0, false), name + "_success");
    }

    private static LLVMValueRef EmitWindowsWriteFile(LlvmCodegenState state, LLVMValueRef handle, LLVMValueRef buffer, LLVMValueRef len, LLVMValueRef bytesWrittenSlot, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef writeFileType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I32, 0, false), bytesWrittenSlot);
        LLVMValueRef writeFilePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(writeFileType, 0),
            state.WindowsWriteFileImport,
            name + "_ptr");
        LLVMValueRef callResult = builder.BuildCall2(
            writeFileType,
            writeFilePtr,
            new[]
            {
                handle,
                buffer,
                len,
                bytesWrittenSlot,
                builder.BuildIntToPtr(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.I8Ptr, name + "_overlapped")
            },
            name);
        return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, callResult, LLVMValueRef.CreateConstInt(state.I32, 0, false), name + "_success");
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
        EmitWriteBytes(
            state,
            EmitStackByteArray(state, System.Text.Encoding.UTF8.GetBytes(whenTrue)),
            LLVMValueRef.CreateConstInt(state.I64, (ulong)whenTrue.Length, false));
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

        EmitWindowsProgramArgsInitialization(state);
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

    private static void EmitWindowsProgramArgsInitialization(LlvmCodegenState state)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef i16 = state.Target.Context.Int16Type;
        LLVMTypeRef i16Ptr = LLVMTypeRef.CreatePointer(i16, 0);
        LLVMTypeRef i16PtrPtr = LLVMTypeRef.CreatePointer(i16Ptr, 0);
        LLVMTypeRef getCommandLineType = LLVMTypeRef.CreateFunction(i16Ptr, []);
        LLVMTypeRef wideCharToMultiByteType = LLVMTypeRef.CreateFunction(state.I32, [state.I32, state.I32, i16Ptr, state.I32, state.I8Ptr, state.I32, state.I8Ptr, state.I8Ptr]);
        LLVMTypeRef localFreeType = LLVMTypeRef.CreateFunction(state.I8Ptr, [state.I8Ptr]);
        LLVMTypeRef commandLineToArgvType = LLVMTypeRef.CreateFunction(i16PtrPtr, [i16Ptr, state.I32Ptr]);

        LLVMValueRef listSlot = builder.BuildAlloca(state.I64, "program_args_list");
        LLVMValueRef argcSlot = builder.BuildAlloca(state.I32, "program_args_argc");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I32, "program_args_index");
        LLVMValueRef wideArgSlot = builder.BuildAlloca(i16Ptr, "program_args_wide_arg");
        LLVMValueRef wideLenSlot = builder.BuildAlloca(state.I32, "program_args_wide_len");
        LLVMValueRef stringRefSlot = builder.BuildAlloca(state.I64, "program_args_string_ref");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), listSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I32, 0, false), argcSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), stringRefSlot);

        LLVMValueRef getCommandLinePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(getCommandLineType, 0),
            state.WindowsGetCommandLineImport,
            "get_command_line_ptr");
        LLVMValueRef commandLinePtr = builder.BuildCall2(
            getCommandLineType,
            getCommandLinePtr,
            Array.Empty<LLVMValueRef>(),
            "command_line");

        LLVMValueRef commandLineToArgvPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(commandLineToArgvType, 0),
            state.WindowsCommandLineToArgvImport,
            "command_line_to_argv_ptr");
        LLVMValueRef argvWide = builder.BuildCall2(
            commandLineToArgvType,
            commandLineToArgvPtr,
            new[] { commandLinePtr, argcSlot },
            "argv_wide");

        var haveArgvBlock = state.Function.AppendBasicBlock("program_args_have_argv");
        var maybeLoopBlock = state.Function.AppendBasicBlock("program_args_maybe_loop");
        var loopCheckBlock = state.Function.AppendBasicBlock("program_args_loop_check");
        var wideArgSetupBlock = state.Function.AppendBasicBlock("program_args_wide_arg_setup");
        var wideLenBodyBlock = state.Function.AppendBasicBlock("program_args_wide_len_body");
        var wideLenIncBlock = state.Function.AppendBasicBlock("program_args_wide_len_inc");
        var convertArgBlock = state.Function.AppendBasicBlock("program_args_convert_arg");
        var createUtf8StringBlock = state.Function.AppendBasicBlock("program_args_create_utf8_string");
        var createEmptyStringBlock = state.Function.AppendBasicBlock("program_args_create_empty_string");
        var linkArgBlock = state.Function.AppendBasicBlock("program_args_link_arg");
        var freeArgvBlock = state.Function.AppendBasicBlock("program_args_free_argv");
        var doneBlock = state.Function.AppendBasicBlock("program_args_done");

        LLVMValueRef hasArgv = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntNE,
            builder.BuildPtrToInt(argvWide, state.I64, "argv_wide_i64"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "program_args_has_argv");
        builder.BuildCondBr(hasArgv, haveArgvBlock, doneBlock);

        builder.PositionAtEnd(haveArgvBlock);
        LLVMValueRef argc = builder.BuildLoad2(state.I32, argcSlot, "program_args_argc_value");
        LLVMValueRef hasUserArgs = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntSGT,
            argc,
            LLVMValueRef.CreateConstInt(state.I32, 1, false),
            "program_args_has_user_args");
        builder.BuildCondBr(hasUserArgs, maybeLoopBlock, freeArgvBlock);

        builder.PositionAtEnd(maybeLoopBlock);
        builder.BuildStore(
            builder.BuildSub(argc, LLVMValueRef.CreateConstInt(state.I32, 1, false), "program_args_start_index"),
            indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I32, indexSlot, "program_args_index_value");
        LLVMValueRef shouldContinue = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntSGT,
            index,
            LLVMValueRef.CreateConstInt(state.I32, 0, false),
            "program_args_continue");
        builder.BuildCondBr(shouldContinue, wideArgSetupBlock, freeArgvBlock);

        builder.PositionAtEnd(wideArgSetupBlock);
        LLVMValueRef wideArgPtrPtr = builder.BuildGEP2(
            i16Ptr,
            argvWide,
            new[] { builder.BuildSExt(index, state.I64, "program_args_index_i64") },
            "program_args_wide_arg_ptr");
        LLVMValueRef wideArgPtr = builder.BuildLoad2(i16Ptr, wideArgPtrPtr, "program_args_wide_arg_value");
        builder.BuildStore(wideArgPtr, wideArgSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I32, 0, false), wideLenSlot);
        builder.BuildBr(wideLenBodyBlock);

        builder.PositionAtEnd(wideLenBodyBlock);
        LLVMValueRef wideLen = builder.BuildLoad2(state.I32, wideLenSlot, "program_args_wide_len_value");
        LLVMValueRef wideCharPtr = builder.BuildGEP2(
            i16,
            builder.BuildLoad2(i16Ptr, wideArgSlot, "program_args_wide_arg_current"),
            new[] { builder.BuildSExt(wideLen, state.I64, "program_args_wide_len_i64") },
            "program_args_wide_char_ptr");
        LLVMValueRef wideChar = builder.BuildLoad2(i16, wideCharPtr, "program_args_wide_char");
        LLVMValueRef atTerminator = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntEQ,
            wideChar,
            LLVMValueRef.CreateConstInt(i16, 0, false),
            "program_args_at_wide_terminator");
        builder.BuildCondBr(atTerminator, convertArgBlock, wideLenIncBlock);

        builder.PositionAtEnd(wideLenIncBlock);
        builder.BuildStore(
            builder.BuildAdd(builder.BuildLoad2(state.I32, wideLenSlot, "program_args_wide_len_before_inc"), LLVMValueRef.CreateConstInt(state.I32, 1, false), "program_args_wide_len_inc"),
            wideLenSlot);
        builder.BuildBr(wideLenBodyBlock);

        builder.PositionAtEnd(convertArgBlock);
        LLVMValueRef wideArg = builder.BuildLoad2(i16Ptr, wideArgSlot, "program_args_wide_arg_for_convert");
        LLVMValueRef wcharCount = builder.BuildLoad2(state.I32, wideLenSlot, "program_args_wchar_count");
        LLVMValueRef wideCharToMultiBytePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(wideCharToMultiByteType, 0),
            state.WindowsWideCharToMultiByteImport,
            "wide_char_to_multi_byte_ptr");
        LLVMValueRef nullI8Ptr = builder.BuildIntToPtr(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.I8Ptr, "null_i8_ptr");
        LLVMValueRef byteCount = builder.BuildCall2(
            wideCharToMultiByteType,
            wideCharToMultiBytePtr,
            new[]
            {
                LLVMValueRef.CreateConstInt(state.I32, Utf8CodePage, false),
                LLVMValueRef.CreateConstInt(state.I32, 0, false),
                wideArg,
                wcharCount,
                nullI8Ptr,
                LLVMValueRef.CreateConstInt(state.I32, 0, false),
                nullI8Ptr,
                nullI8Ptr
            },
            "program_args_byte_count");
        LLVMValueRef hasBytes = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntSGT,
            byteCount,
            LLVMValueRef.CreateConstInt(state.I32, 0, false),
            "program_args_has_bytes");
        builder.BuildCondBr(hasBytes, createUtf8StringBlock, createEmptyStringBlock);

        builder.PositionAtEnd(createUtf8StringBlock);
        LLVMValueRef stringRef = EmitAllocDynamic(
            state,
            builder.BuildAdd(builder.BuildZExt(byteCount, state.I64, "program_args_byte_count_i64"), LLVMValueRef.CreateConstInt(state.I64, 8, false), "program_args_string_bytes"));
        StoreMemory(state, stringRef, 0, builder.BuildZExt(byteCount, state.I64, "program_args_string_len"), "program_args_string_len");
        LLVMValueRef stringDest = GetStringBytesPointer(state, stringRef, "program_args_string_dest");
        builder.BuildCall2(
            wideCharToMultiByteType,
            wideCharToMultiBytePtr,
            new[]
            {
                LLVMValueRef.CreateConstInt(state.I32, Utf8CodePage, false),
                LLVMValueRef.CreateConstInt(state.I32, 0, false),
                wideArg,
                wcharCount,
                stringDest,
                byteCount,
                nullI8Ptr,
                nullI8Ptr
            },
            "program_args_copy_utf8");
        builder.BuildStore(stringRef, stringRefSlot);
        builder.BuildBr(linkArgBlock);

        builder.PositionAtEnd(createEmptyStringBlock);
        LLVMValueRef emptyStringRef = EmitAlloc(state, 8);
        StoreMemory(state, emptyStringRef, 0, LLVMValueRef.CreateConstInt(state.I64, 0, false), "program_args_empty_string_len");
        builder.BuildStore(emptyStringRef, stringRefSlot);
        builder.BuildBr(linkArgBlock);

        builder.PositionAtEnd(linkArgBlock);
        LLVMValueRef consRef = EmitAlloc(state, 16);
        StoreMemory(state, consRef, 0, builder.BuildLoad2(state.I64, stringRefSlot, "program_args_string_ref_value"), "program_args_cons_head");
        StoreMemory(state, consRef, 8, builder.BuildLoad2(state.I64, listSlot, "program_args_prev_list"), "program_args_cons_tail");
        builder.BuildStore(consRef, listSlot);
        builder.BuildStore(
            builder.BuildSub(builder.BuildLoad2(state.I32, indexSlot, "program_args_index_before_dec"), LLVMValueRef.CreateConstInt(state.I32, 1, false), "program_args_index_dec"),
            indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(freeArgvBlock);
        LLVMValueRef localFreePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(localFreeType, 0),
            state.WindowsLocalFreeImport,
            "local_free_ptr");
        builder.BuildCall2(
            localFreeType,
            localFreePtr,
            new[] { builder.BuildBitCast(argvWide, state.I8Ptr, "argv_wide_hlocal") },
            "program_args_local_free");
        builder.BuildBr(doneBlock);

        builder.PositionAtEnd(doneBlock);
        builder.BuildStore(builder.BuildLoad2(state.I64, listSlot, "program_args_final_list"), state.ProgramArgsSlot);
    }

    private static void EmitWriteBytes(LlvmCodegenState state, LLVMValueRef bytePtr, LLVMValueRef len)
    {
        if (state.Flavor == LlvmCodegenFlavor.Linux)
        {
            EmitSyscall(
                state,
                SyscallWrite,
                LLVMValueRef.CreateConstInt(state.I64, 1, false),
                state.Target.Builder.BuildPtrToInt(bytePtr, state.I64, "write_ptr_i64"),
                len,
                "sys_write");
            return;
        }

        EmitWindowsWriteBytes(state, bytePtr, len);
    }

    private static LLVMValueRef EmitWindowsGetStdHandle(LlvmCodegenState state, uint handleKind, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef getStdHandleType = LLVMTypeRef.CreateFunction(state.I64, [state.I32]);
        LLVMValueRef getStdHandlePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(getStdHandleType, 0),
            state.WindowsGetStdHandleImport,
            name + "_ptr");
        return builder.BuildCall2(
            getStdHandleType,
            getStdHandlePtr,
            new[] { LLVMValueRef.CreateConstInt(state.I32, handleKind, true) },
            name);
    }

    private static LLVMValueRef EmitWindowsReadByte(LlvmCodegenState state, LLVMValueRef stdinHandle, LLVMValueRef byteSlot, LLVMValueRef bytesReadSlot)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef readFileType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I32, 0, false), bytesReadSlot);
        LLVMValueRef readFilePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(readFileType, 0),
            state.WindowsReadFileImport,
            "read_file_ptr");
        builder.BuildCall2(
            readFileType,
            readFilePtr,
            new[]
            {
                stdinHandle,
                byteSlot,
                LLVMValueRef.CreateConstInt(state.I32, 1, false),
                bytesReadSlot,
                builder.BuildIntToPtr(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.I8Ptr, "null_overlapped")
            },
            "read_file");
        return builder.BuildZExt(builder.BuildLoad2(state.I32, bytesReadSlot, "read_line_bytes_read_value"), state.I64, "read_line_bytes_read_i64");
    }

    private static void EmitWindowsWriteBytes(LlvmCodegenState state, LLVMValueRef bytePtr, LLVMValueRef len)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef writeFileType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        LLVMValueRef stdoutHandle = EmitWindowsGetStdHandle(state, StdOutputHandle, "stdout_handle");
        LLVMValueRef bytesWritten = builder.BuildAlloca(state.I32, "bytes_written");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I32, 0, false), bytesWritten);
        LLVMValueRef writeFilePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(writeFileType, 0),
            state.WindowsWriteFileImport,
            "write_file_ptr");
        builder.BuildCall2(
            writeFileType,
            writeFilePtr,
            new[]
            {
                stdoutHandle,
                bytePtr,
                builder.BuildTrunc(NormalizeToI64(state, len), state.I32, "write_len_i32"),
                bytesWritten,
                builder.BuildIntToPtr(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.I8Ptr, "null_overlapped")
            },
            "write_file");
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
        LLVMTypeRef I32,
        LLVMTypeRef I8,
        LLVMTypeRef F64,
        LLVMTypeRef I8Ptr,
        LLVMTypeRef I32Ptr,
        LLVMTypeRef I64Ptr,
        LLVMValueRef EntryStackPointer,
        LLVMValueRef WindowsGetStdHandleImport,
        LLVMValueRef WindowsWriteFileImport,
        LLVMValueRef WindowsReadFileImport,
        LLVMValueRef WindowsCreateFileImport,
        LLVMValueRef WindowsCloseHandleImport,
        LLVMValueRef WindowsGetFileAttributesImport,
        LLVMValueRef WindowsWsaStartupImport,
        LLVMValueRef WindowsSocketImport,
        LLVMValueRef WindowsConnectImport,
        LLVMValueRef WindowsSendImport,
        LLVMValueRef WindowsRecvImport,
        LLVMValueRef WindowsCloseSocketImport,
        LLVMValueRef WindowsExitProcessImport,
        LLVMValueRef WindowsGetCommandLineImport,
        LLVMValueRef WindowsWideCharToMultiByteImport,
        LLVMValueRef WindowsLocalFreeImport,
        LLVMValueRef WindowsCommandLineToArgvImport,
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

    private enum LlvmCodegenFlavor
    {
        Linux,
        Windows
    }
}
