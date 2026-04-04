using System.Buffers.Binary;
using Ashes.Backend.Backends;
using Ashes.Semantics;
using LLVMSharp.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
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

        if (usesWindowsStdout || usesWindowsFileOps)
        {
            LLVMTypeRef writeFileType = LLVMTypeRef.CreateFunction(i32, [i64, i8Ptr, i32, i32Ptr, i8Ptr]);
            windowsWriteFileImport = target.Module.AddGlobal(LLVMTypeRef.CreatePointer(writeFileType, 0), "__imp_WriteFile");
            windowsWriteFileImport.Linkage = LLVMLinkage.LLVMExternalLinkage;
        }

        if (usesWindowsReadLine || usesWindowsFileOps)
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
            // Constant strings must remain valid when closures return them or wrap them in ADTs.
            IrInst.LoadConstStr loadConstStr => StoreTemp(state, loadConstStr.Target, EmitHeapStringLiteral(state, state.StringLiterals[loadConstStr.StrLabel])),
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
