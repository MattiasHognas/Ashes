using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Ashes.Backend.Backends;
using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private const int HeapChunkBytes = 1024 * 1024 * 4;
    private const int InputBufSize = 64 * 1024;
    private const int MaxFileReadBytes = 1024 * 1024;
    private const uint Utf8CodePage = 65001;
    private const uint StdOutputHandle = 0xFFFFFFF5;
    private const uint StdInputHandle = 0xFFFFFFF6;
    private const long SyscallRead = 0;
    private const long SyscallWrite = 1;
    private const long SyscallOpen = 2;
    private const long SyscallClose = 3;
    private const long SyscallMmap = 9;
    private const long SyscallLseek = 8;
    private const long SyscallSocket = 41;
    private const long SyscallConnect = 42;
    private const long SyscallNanosleep = 35;
    private const long SyscallClockGettime = 228;
    private const long SyscallExit = 60;

    // AArch64 Linux syscall numbers
    private const long Arm64SyscallClose = 57;
    private const long Arm64SyscallOpenat = 56;
    private const long Arm64SyscallLseek = 62;
    private const long Arm64SyscallMmap = 222;
    private const long Arm64SyscallRead = 63;
    private const long Arm64SyscallWrite = 64;
    private const long Arm64SyscallExit = 93;
    private const long Arm64SyscallSocket = 198;
    private const long Arm64SyscallConnect = 203;
    private const long Arm64SyscallNanosleep = 101;
    private const long Arm64SyscallClockGettime = 113;
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
            Backends.TargetIds.LinuxArm64 => CompileLinuxArm64(program, options),
            Backends.TargetIds.WindowsX64 => CompileWindows(program, options),
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unknown target '{targetId}'."),
        };
    }

    private static byte[] CompileWindows(IrProgram program, BackendCompileOptions options)
    {
        using LlvmTargetContext target = LlvmTargetSetup.Create(Backends.TargetIds.WindowsX64, options.OptimizationLevel);
        var literals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        EmitProgramModule(target, program, "entry", LlvmCodegenFlavor.WindowsX64, options);

        VerifyModule(target);
        byte[] objectBytes = EmitObjectCode(target);
        return LlvmImageLinker.LinkWindowsExecutable(objectBytes, "entry");
    }

    private static byte[] CompileLinux(IrProgram program, BackendCompileOptions options)
    {
        using LlvmTargetContext target = LlvmTargetSetup.Create(Backends.TargetIds.LinuxX64, options.OptimizationLevel);
        var literals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        EmitProgramModule(target, program, "entry", LlvmCodegenFlavor.LinuxX64, options);

        VerifyModule(target);
        byte[] objectBytes = EmitObjectCode(target);
        return LlvmImageLinker.LinkLinuxExecutable(objectBytes, "entry");
    }

    private static byte[] CompileLinuxArm64(IrProgram program, BackendCompileOptions options)
    {
        using LlvmTargetContext target = LlvmTargetSetup.Create(Backends.TargetIds.LinuxArm64, options.OptimizationLevel);
        var literals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        EmitProgramModule(target, program, "entry", LlvmCodegenFlavor.LinuxArm64, options);

        VerifyModule(target);
        byte[] objectBytes = EmitObjectCode(target);
        return LlvmImageLinker.LinkLinuxArm64Executable(objectBytes, "entry");
    }

    private static void VerifyModule(LlvmTargetContext target)
    {
        int verifyErr = LlvmApi.VerifyModule(target.Module, LlvmVerifierFailureAction.ReturnStatus, out nint verifyMsg);
        if (verifyErr != 0)
        {
            string verifyError = Marshal.PtrToStringAnsi(verifyMsg) ?? "unknown error";
            LlvmApi.DisposeMessage(verifyMsg);
            throw new InvalidOperationException($"LLVM module verification failed: {verifyError}");
        }

        if (verifyMsg != 0)
        {
            LlvmApi.DisposeMessage(verifyMsg);
        }
    }

    /// <summary>
    /// Runs the LLVM new pass manager optimization pipeline on the module.
    /// Uses the standard "default&lt;ON&gt;" pipeline string which includes:
    /// instcombine, simplifycfg, mem2reg, dead-code elimination, inlining,
    /// loop optimizations, and other standard LLVM passes.
    /// At O0 no passes are run — codegen output is used as-is.
    ///
    /// NOTE: Currently not wired into the compilation pipeline because the
    /// custom ELF/PE linkers do not yet handle the additional relocation
    /// types and sections that module-level optimization passes produce.
    /// The codegen optimization level (set via LlvmCodeGenOptLevel in
    /// LlvmTargetSetup) still applies LLVM optimizations during instruction
    /// selection and register allocation. Full pass manager integration
    /// will be enabled when the linker supports a broader relocation set.
    /// </summary>
    internal static void RunLlvmOptimizationPasses(LlvmTargetContext target, Backends.BackendOptimizationLevel level)
    {
        if (level == Backends.BackendOptimizationLevel.O0)
        {
            return; // No optimization at O0
        }

        string passString = level switch
        {
            Backends.BackendOptimizationLevel.O1 => "default<O1>",
            Backends.BackendOptimizationLevel.O2 => "default<O2>",
            Backends.BackendOptimizationLevel.O3 => "default<O3>",
            _ => "default<O2>",
        };

        LlvmPassBuilderOptionsHandle passOptions = LlvmApi.CreatePassBuilderOptions();
        try
        {
            int err = LlvmApi.RunPasses(target.Module, passString, target.TargetMachine, passOptions);
            if (err != 0)
            {
                // Non-fatal: if passes fail, continue with unoptimized code.
                // This can happen in edge cases; the code is still correct.
            }
        }
        finally
        {
            LlvmApi.DisposePassBuilderOptions(passOptions);
        }
    }

    private static byte[] EmitObjectCode(LlvmTargetContext target)
    {
        int err = LlvmApi.TargetMachineEmitToMemoryBuffer(
            target.TargetMachine, target.Module, LlvmCodeGenFileType.Object, out nint errMsg, out nint memBuf);

        if (err != 0)
        {
            string msg = Marshal.PtrToStringAnsi(errMsg) ?? "unknown error";
            LlvmApi.DisposeMessage(errMsg);
            throw new InvalidOperationException($"LLVM emit failed: {msg}");
        }

        try
        {
            nint start = LlvmApi.GetBufferStart(memBuf);
            nint size = LlvmApi.GetBufferSize(memBuf);
            byte[] objectCode = new byte[(int)size];
            Marshal.Copy(start, objectCode, 0, (int)size);
            return objectCode;
        }
        finally
        {
            LlvmApi.DisposeMemoryBuffer(memBuf);
        }
    }

    private static void EmitProgramModule(
        LlvmTargetContext target,
        IrProgram program,
        string entryFunctionName,
        LlvmCodegenFlavor flavor,
        Backends.BackendCompileOptions options)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmTypeHandle i32 = LlvmApi.Int32TypeInContext(target.Context);
        LlvmTypeHandle i8 = LlvmApi.Int8TypeInContext(target.Context);
        LlvmTypeHandle f64 = LlvmApi.DoubleTypeInContext(target.Context);
        LlvmTypeHandle voidType = LlvmApi.VoidTypeInContext(target.Context);
        LlvmTypeHandle i8Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
        LlvmTypeHandle i32Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
        LlvmTypeHandle i64Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
        var stringLiterals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        LlvmTypeHandle closureFunctionType = LlvmApi.FunctionType(i64, [i64, i64]);
        bool usesProgramArgs = ProgramUsesInstruction<IrInst.LoadProgramArgs>(program);
        bool usesReadLine = ProgramUsesInstruction<IrInst.ReadLine>(program);
        bool usesWindowsStdout = flavor == LlvmCodegenFlavor.WindowsX64
            && (ProgramUsesInstruction<IrInst.PrintInt>(program)
                || ProgramUsesInstruction<IrInst.PrintStr>(program)
                || ProgramUsesInstruction<IrInst.WriteStr>(program)
                || ProgramUsesInstruction<IrInst.PrintBool>(program)
                || ProgramUsesInstruction<IrInst.PanicStr>(program)
                || usesReadLine);
        bool usesWindowsExitProcess = flavor == LlvmCodegenFlavor.WindowsX64;
        bool usesWindowsProgramArgs = flavor == LlvmCodegenFlavor.WindowsX64
            && usesProgramArgs;
        bool usesWindowsReadLine = flavor == LlvmCodegenFlavor.WindowsX64
            && usesReadLine;
        bool usesWindowsFileOps = flavor == LlvmCodegenFlavor.WindowsX64
            && (ProgramUsesInstruction<IrInst.FileReadText>(program)
                || ProgramUsesInstruction<IrInst.FileWriteText>(program)
                || ProgramUsesInstruction<IrInst.FileExists>(program));
        bool usesWindowsSockets = flavor == LlvmCodegenFlavor.WindowsX64
            && (ProgramUsesInstruction<IrInst.HttpGet>(program)
                || ProgramUsesInstruction<IrInst.HttpPost>(program)
                || ProgramUsesInstruction<IrInst.NetTcpConnect>(program)
                || ProgramUsesInstruction<IrInst.NetTcpSend>(program)
                || ProgramUsesInstruction<IrInst.NetTcpReceive>(program)
                || ProgramUsesInstruction<IrInst.NetTcpClose>(program)
                || ProgramUsesInstruction<IrInst.Drop>(program));
        bool usesWindowsSleep = flavor == LlvmCodegenFlavor.WindowsX64
            && (ProgramUsesInstruction<IrInst.AsyncSleep>(program)
                || ProgramUsesInstruction<IrInst.RunTask>(program)
                || ProgramUsesInstruction<IrInst.AsyncAll>(program)
                || ProgramUsesInstruction<IrInst.AsyncRace>(program));
        LlvmValueHandle windowsGetStdHandleImport = default;
        LlvmValueHandle windowsWriteFileImport = default;
        LlvmValueHandle windowsReadFileImport = default;
        LlvmValueHandle windowsCreateFileImport = default;
        LlvmValueHandle windowsCloseHandleImport = default;
        LlvmValueHandle windowsGetFileAttributesImport = default;
        LlvmValueHandle windowsWsaStartupImport = default;
        LlvmValueHandle windowsSocketImport = default;
        LlvmValueHandle windowsConnectImport = default;
        LlvmValueHandle windowsSendImport = default;
        LlvmValueHandle windowsRecvImport = default;
        LlvmValueHandle windowsCloseSocketImport = default;
        LlvmValueHandle windowsExitProcessImport = default;
        LlvmValueHandle windowsGetCommandLineImport = default;
        LlvmValueHandle windowsWideCharToMultiByteImport = default;
        LlvmValueHandle windowsLocalFreeImport = default;
        LlvmValueHandle windowsCommandLineToArgvImport = default;
        LlvmValueHandle windowsSleepImport = default;
        LlvmValueHandle windowsVirtualAllocImport = default;
        LlvmValueHandle heapCursorGlobal = LlvmApi.AddGlobal(target.Module, i64, "__ashes_heap_cursor");
        LlvmApi.SetLinkage(heapCursorGlobal, LlvmLinkage.Internal);
        LlvmApi.SetInitializer(heapCursorGlobal, LlvmApi.ConstInt(i64, 0, 0));
        LlvmValueHandle heapEndGlobal = LlvmApi.AddGlobal(target.Module, i64, "__ashes_heap_end");
        LlvmApi.SetLinkage(heapEndGlobal, LlvmLinkage.Internal);
        LlvmApi.SetInitializer(heapEndGlobal, LlvmApi.ConstInt(i64, 0, 0));
        if (usesWindowsStdout || usesWindowsReadLine)
        {
            LlvmTypeHandle getStdHandleType = LlvmApi.FunctionType(i64, [i32]);
            windowsGetStdHandleImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_GetStdHandle");
            LlvmApi.SetLinkage(windowsGetStdHandleImport, LlvmLinkage.External);
        }

        if (usesWindowsStdout || usesWindowsFileOps)
        {
            LlvmTypeHandle writeFileType = LlvmApi.FunctionType(i32, [i64, i8Ptr, i32, i32Ptr, i8Ptr]);
            windowsWriteFileImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_WriteFile");
            LlvmApi.SetLinkage(windowsWriteFileImport, LlvmLinkage.External);
        }

        if (usesWindowsReadLine || usesWindowsFileOps)
        {
            LlvmTypeHandle readFileType = LlvmApi.FunctionType(i32, [i64, i8Ptr, i32, i32Ptr, i8Ptr]);
            windowsReadFileImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_ReadFile");
            LlvmApi.SetLinkage(windowsReadFileImport, LlvmLinkage.External);
        }

        if (usesWindowsFileOps)
        {
            LlvmTypeHandle createFileType = LlvmApi.FunctionType(i64, [i8Ptr, i32, i32, i8Ptr, i32, i32, i64]);
            LlvmTypeHandle closeHandleType = LlvmApi.FunctionType(i32, [i64]);
            LlvmTypeHandle getFileAttributesType = LlvmApi.FunctionType(i32, [i8Ptr]);
            windowsCreateFileImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_CreateFileA");
            LlvmApi.SetLinkage(windowsCreateFileImport, LlvmLinkage.External);
            windowsCloseHandleImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_CloseHandle");
            LlvmApi.SetLinkage(windowsCloseHandleImport, LlvmLinkage.External);
            windowsGetFileAttributesImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_GetFileAttributesA");
            LlvmApi.SetLinkage(windowsGetFileAttributesImport, LlvmLinkage.External);
        }

        if (usesWindowsSockets)
        {
            LlvmTypeHandle wsaStartupType = LlvmApi.FunctionType(i32, [LlvmApi.Int16TypeInContext(target.Context), i8Ptr]);
            LlvmTypeHandle socketType = LlvmApi.FunctionType(i64, [i32, i32, i32]);
            LlvmTypeHandle connectType = LlvmApi.FunctionType(i32, [i64, i8Ptr, i32]);
            LlvmTypeHandle sendType = LlvmApi.FunctionType(i32, [i64, i8Ptr, i32, i32]);
            LlvmTypeHandle recvType = LlvmApi.FunctionType(i32, [i64, i8Ptr, i32, i32]);
            LlvmTypeHandle closeSocketType = LlvmApi.FunctionType(i32, [i64]);
            windowsWsaStartupImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_WSAStartup");
            LlvmApi.SetLinkage(windowsWsaStartupImport, LlvmLinkage.External);
            windowsSocketImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_socket");
            LlvmApi.SetLinkage(windowsSocketImport, LlvmLinkage.External);
            windowsConnectImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_connect");
            LlvmApi.SetLinkage(windowsConnectImport, LlvmLinkage.External);
            windowsSendImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_send");
            LlvmApi.SetLinkage(windowsSendImport, LlvmLinkage.External);
            windowsRecvImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_recv");
            LlvmApi.SetLinkage(windowsRecvImport, LlvmLinkage.External);
            windowsCloseSocketImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_closesocket");
            LlvmApi.SetLinkage(windowsCloseSocketImport, LlvmLinkage.External);
        }

        if (usesWindowsExitProcess)
        {
            LlvmTypeHandle exitProcessType = LlvmApi.FunctionType(voidType, [i32]);
            windowsExitProcessImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_ExitProcess");
            LlvmApi.SetLinkage(windowsExitProcessImport, LlvmLinkage.External);
        }

        if (flavor == LlvmCodegenFlavor.WindowsX64)
        {
            windowsVirtualAllocImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_VirtualAlloc");
            LlvmApi.SetLinkage(windowsVirtualAllocImport, LlvmLinkage.External);
        }

        if (usesWindowsSleep)
        {
            windowsSleepImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_Sleep");
            LlvmApi.SetLinkage(windowsSleepImport, LlvmLinkage.External);
        }

        if (usesWindowsProgramArgs)
        {
            LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(target.Context);
            LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
            LlvmTypeHandle i16PtrPtr = LlvmApi.PointerTypeInContext(target.Context, 0);
            LlvmTypeHandle getCommandLineType = LlvmApi.FunctionType(i16Ptr, []);
            LlvmTypeHandle wideCharToMultiByteType = LlvmApi.FunctionType(i32, [i32, i32, i16Ptr, i32, i8Ptr, i32, i8Ptr, i8Ptr]);
            LlvmTypeHandle localFreeType = LlvmApi.FunctionType(i8Ptr, [i8Ptr]);
            LlvmTypeHandle commandLineToArgvType = LlvmApi.FunctionType(i16PtrPtr, [i16Ptr, i32Ptr]);

            windowsGetCommandLineImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_GetCommandLineW");
            LlvmApi.SetLinkage(windowsGetCommandLineImport, LlvmLinkage.External);
            windowsWideCharToMultiByteImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_WideCharToMultiByte");
            LlvmApi.SetLinkage(windowsWideCharToMultiByteImport, LlvmLinkage.External);
            windowsLocalFreeImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_LocalFree");
            LlvmApi.SetLinkage(windowsLocalFreeImport, LlvmLinkage.External);
            windowsCommandLineToArgvImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_CommandLineToArgvW");
            LlvmApi.SetLinkage(windowsCommandLineToArgvImport, LlvmLinkage.External);
        }

        LlvmValueHandle entryFunction = LlvmApi.AddFunction(target.Module,
            entryFunctionName,
            IsLinuxFlavor(flavor)
                ? LlvmApi.FunctionType(voidType, [i64])
                : LlvmApi.FunctionType(voidType, []));
        LlvmApi.SetLinkage(entryFunction, LlvmLinkage.External);

        var liftedFunctions = new Dictionary<string, LlvmValueHandle>(StringComparer.Ordinal);
        foreach (IrFunction function in program.Functions)
        {
            LlvmValueHandle llvmFunction = LlvmApi.AddFunction(target.Module, function.Label, closureFunctionType);
            LlvmApi.SetLinkage(llvmFunction, LlvmLinkage.Internal);
            liftedFunctions.Add(function.Label, llvmFunction);
        }

        // Debug info setup (Phase 2b)
        using var dbg = CreateDebugInfoContext(target, options, program);
        if (dbg is not null)
        {
            SetupFunctionDebugInfo(dbg, entryFunction, program.EntryFunction);
            foreach (IrFunction function in program.Functions)
            {
                SetupFunctionDebugInfo(dbg, liftedFunctions[function.Label], function);
            }
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
            heapCursorGlobal,
            heapEndGlobal,
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
            windowsSleepImport,
            windowsVirtualAllocImport,
            isEntry: true,
            debugContext: dbg);

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
                heapCursorGlobal,
                heapEndGlobal,
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
                windowsSleepImport,
                windowsVirtualAllocImport,
                isEntry: false,
                debugContext: dbg);
        }

        dbg?.FinalizeDebugInfo();
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
        LlvmValueHandle llvmFunction,
        IrFunction function,
        IReadOnlyDictionary<string, string> stringLiterals,
        IReadOnlyDictionary<string, LlvmValueHandle> liftedFunctions,
        LlvmCodegenFlavor flavor,
        bool usesProgramArgs,
        LlvmTypeHandle i32,
        LlvmTypeHandle i32Ptr,
        LlvmValueHandle heapCursorGlobal,
        LlvmValueHandle heapEndGlobal,
        LlvmValueHandle windowsGetStdHandleImport,
        LlvmValueHandle windowsWriteFileImport,
        LlvmValueHandle windowsReadFileImport,
        LlvmValueHandle windowsCreateFileImport,
        LlvmValueHandle windowsCloseHandleImport,
        LlvmValueHandle windowsGetFileAttributesImport,
        LlvmValueHandle windowsWsaStartupImport,
        LlvmValueHandle windowsSocketImport,
        LlvmValueHandle windowsConnectImport,
        LlvmValueHandle windowsSendImport,
        LlvmValueHandle windowsRecvImport,
        LlvmValueHandle windowsCloseSocketImport,
        LlvmValueHandle windowsExitProcessImport,
        LlvmValueHandle windowsGetCommandLineImport,
        LlvmValueHandle windowsWideCharToMultiByteImport,
        LlvmValueHandle windowsLocalFreeImport,
        LlvmValueHandle windowsCommandLineToArgvImport,
        LlvmValueHandle windowsSleepImport,
        LlvmValueHandle windowsVirtualAllocImport,
        bool isEntry,
        DebugInfoContext? debugContext = null)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmTypeHandle i8 = LlvmApi.Int8TypeInContext(target.Context);
        LlvmTypeHandle f64 = LlvmApi.DoubleTypeInContext(target.Context);
        LlvmTypeHandle i8Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
        LlvmTypeHandle i64Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);

        LlvmBasicBlockHandle entryBlock = LlvmApi.AppendBasicBlockInContext(target.Context, llvmFunction, "entry");
        LlvmApi.PositionBuilderAtEnd(target.Builder, entryBlock);

        LlvmValueHandle entryStackPointer = isEntry && IsLinuxFlavor(flavor)
            ? LlvmApi.GetParam(llvmFunction, 0)
            : default;

        var tempSlots = new LlvmValueHandle[function.TempCount];
        for (int i = 0; i < tempSlots.Length; i++)
        {
            tempSlots[i] = LlvmApi.BuildAlloca(target.Builder, i64, $"tmp_{i}");
            LlvmApi.BuildStore(target.Builder, LlvmApi.ConstInt(i64, 0, 0), tempSlots[i]);
        }

        var localSlots = new LlvmValueHandle[function.LocalCount];
        for (int i = 0; i < localSlots.Length; i++)
        {
            localSlots[i] = LlvmApi.BuildAlloca(target.Builder, i64, $"local_{i}");
            LlvmApi.BuildStore(target.Builder, LlvmApi.ConstInt(i64, 0, 0), localSlots[i]);
        }

        LlvmValueHandle programArgsSlot = LlvmApi.BuildAlloca(target.Builder, i64, "program_args");
        LlvmApi.BuildStore(target.Builder, LlvmApi.ConstInt(i64, 0, 0), programArgsSlot);

        if (!isEntry && function.HasEnvAndArgParams)
        {
            LlvmApi.BuildStore(target.Builder, LlvmApi.GetParam(llvmFunction, 0), localSlots[0]);
            LlvmApi.BuildStore(target.Builder, LlvmApi.GetParam(llvmFunction, 1), localSlots[1]);
        }

        var labelBlocks = new Dictionary<string, LlvmBasicBlockHandle>(StringComparer.Ordinal);
        foreach (IrInst.Label label in function.Instructions.OfType<IrInst.Label>())
        {
            labelBlocks[label.Name] = LlvmApi.AppendBasicBlockInContext(target.Context, llvmFunction, label.Name);
        }

        var fallthroughBlocks = new Dictionary<int, LlvmBasicBlockHandle>();
        var state = new LlvmCodegenState(
            target,
            llvmFunction,
            stringLiterals,
            liftedFunctions,
            programArgsSlot,
            tempSlots,
            localSlots,
            heapCursorGlobal,
            heapEndGlobal,
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
            windowsSleepImport,
            windowsVirtualAllocImport,
            flavor,
            usesProgramArgs,
            isEntry);

        if (isEntry)
        {
            EmitHeapChunkInit(state);
        }

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
                    LlvmApi.BuildBr(target.Builder, state.GetLabelBlock(label.Name));
                }

                LlvmApi.PositionBuilderAtEnd(target.Builder, state.GetLabelBlock(label.Name));
                terminated = false;
                continue;
            }

            if (terminated)
            {
                LlvmApi.PositionBuilderAtEnd(target.Builder, state.GetOrCreateFallthroughBlock(index));
                terminated = false;
            }

            EmitInstructionDebugLocation(debugContext, target.Builder, instruction, function.Label);
            terminated = EmitInstruction(state, instruction, index);
        }

        if (!terminated)
        {
            if (state.IsEntry)
            {
                if (IsLinuxFlavor(state.Flavor))
                {
                    EmitExit(state, LlvmApi.ConstInt(i64, 0, 0));
                }
                else
                {
                    LlvmApi.BuildRetVoid(target.Builder);
                }
            }
            else
            {
                LlvmApi.BuildRet(target.Builder, LlvmApi.ConstInt(i64, 0, 0));
            }
        }
    }

    private static bool EmitInstruction(LlvmCodegenState state, IrInst instruction, int index)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        return instruction switch
        {
            IrInst.LoadConstInt loadConstInt => StoreTemp(state, loadConstInt.Target, LlvmApi.ConstInt(state.I64, unchecked((ulong)loadConstInt.Value), 1)),
            IrInst.LoadConstFloat loadConstFloat => StoreTemp(state, loadConstFloat.Target, LlvmApi.ConstReal(state.F64, loadConstFloat.Value)),
            IrInst.LoadConstBool loadConstBool => StoreTemp(state, loadConstBool.Target, LlvmApi.ConstInt(state.I64, loadConstBool.Value ? 1UL : 0UL, 0)),
            // Constant strings must remain valid when closures return them or wrap them in ADTs.
            IrInst.LoadConstStr loadConstStr => StoreTemp(state, loadConstStr.Target, EmitHeapStringLiteral(state, state.StringLiterals[loadConstStr.StrLabel])),
            IrInst.LoadProgramArgs loadProgramArgs => StoreTemp(state, loadProgramArgs.Target, LlvmApi.BuildLoad2(builder, state.I64, state.ProgramArgsSlot, "program_args")),
            IrInst.ReadLine readLine => StoreTemp(state, readLine.Target, EmitReadLine(state)),
            IrInst.FileReadText fileReadText => StoreTemp(state, fileReadText.Target, EmitFileReadText(state, LoadTemp(state, fileReadText.PathTemp))),
            IrInst.FileWriteText fileWriteText => StoreTemp(state, fileWriteText.Target, EmitFileWriteText(state, LoadTemp(state, fileWriteText.PathTemp), LoadTemp(state, fileWriteText.TextTemp))),
            IrInst.FileExists fileExists => StoreTemp(state, fileExists.Target, EmitFileExists(state, LoadTemp(state, fileExists.PathTemp))),
            IrInst.HttpGet httpGet => StoreTemp(state, httpGet.Target, EmitHttpRequest(state, LoadTemp(state, httpGet.UrlTemp), LlvmApi.ConstInt(state.I64, 0, 0), hasBody: false)),
            IrInst.HttpPost httpPost => StoreTemp(state, httpPost.Target, EmitHttpRequest(state, LoadTemp(state, httpPost.UrlTemp), LoadTemp(state, httpPost.BodyTemp), hasBody: true)),
            IrInst.NetTcpConnect tcpConnect => StoreTemp(state, tcpConnect.Target, EmitTcpConnect(state, LoadTemp(state, tcpConnect.HostTemp), LoadTemp(state, tcpConnect.PortTemp))),
            IrInst.NetTcpSend tcpSend => StoreTemp(state, tcpSend.Target, EmitTcpSend(state, LoadTemp(state, tcpSend.SocketTemp), LoadTemp(state, tcpSend.TextTemp))),
            IrInst.NetTcpReceive tcpReceive => StoreTemp(state, tcpReceive.Target, EmitTcpReceive(state, LoadTemp(state, tcpReceive.SocketTemp), LoadTemp(state, tcpReceive.MaxBytesTemp))),
            IrInst.NetTcpClose tcpClose => StoreTemp(state, tcpClose.Target, EmitTcpClose(state, LoadTemp(state, tcpClose.SocketTemp))),
            IrInst.Drop drop => EmitDrop(state, LoadTemp(state, drop.SourceTemp), drop.TypeName),
            // Borrow: non-owning reference — simple value pass-through (pointer copy).
            // No ownership transfer, no drop responsibility. The owning scope still drops.
            IrInst.Borrow borrow => StoreTemp(state, borrow.Target, LoadTemp(state, borrow.SourceTemp)),
            // CreateTask: allocate task struct with coroutine function + captures.
            IrInst.CreateTask createTask => StoreTemp(state, createTask.Target,
                EmitCreateTask(state, LoadTemp(state, createTask.ClosureTemp),
                    createTask.StateStructSize, createTask.CaptureCount)),
            // CreateCompletedTask: pre-completed task with result already available.
            IrInst.CreateCompletedTask cct => StoreTemp(state, cct.Target,
                EmitCreateCompletedTask(state, LoadTemp(state, cct.ResultTemp))),
            // AwaitTask: should not appear after state machine transform. Pass-through for safety.
            IrInst.AwaitTask awaitTask => StoreTemp(state, awaitTask.Target, LoadTemp(state, awaitTask.TaskTemp)),
            // RunTask: drive task to completion via event loop.
            IrInst.RunTask runTask => StoreTemp(state, runTask.Target,
                EmitRunTask(state, LoadTemp(state, runTask.TaskTemp))),
            // AsyncSleep: create a sleep task with a timer deadline.
            IrInst.AsyncSleep asyncSleep => StoreTemp(state, asyncSleep.Target,
                EmitAsyncSleep(state, LoadTemp(state, asyncSleep.MillisecondsTemp))),
            // AsyncAll: run all tasks in a list, collect results.
            IrInst.AsyncAll asyncAll => StoreTemp(state, asyncAll.Target,
                EmitAsyncAll(state, LoadTemp(state, asyncAll.TaskListTemp))),
            // AsyncRace: run the first task in a list, return its result.
            IrInst.AsyncRace asyncRace => StoreTemp(state, asyncRace.Target,
                EmitAsyncRace(state, LoadTemp(state, asyncRace.TaskListTemp))),
            // Suspend/Resume: state machine annotations — no-ops in codegen.
            // The actual save/restore is done by StoreMemOffset/LoadMemOffset around them.
            IrInst.Suspend => false,
            IrInst.Resume => false,
            IrInst.LoadLocal loadLocal => StoreTemp(state, loadLocal.Target, LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[loadLocal.Slot], $"load_local_{loadLocal.Slot}")),
            IrInst.StoreLocal storeLocal => StoreLocal(state, storeLocal.Slot, LoadTemp(state, storeLocal.Source)),
            IrInst.LoadEnv loadEnv => StoreTemp(state, loadEnv.Target, LlvmApi.BuildLoad2(builder, state.I64, GetMemoryPointer(state, LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[0], "env_ptr"), loadEnv.Index * 8, $"load_env_{loadEnv.Index}_ptr"), $"load_env_{loadEnv.Index}")),
            IrInst.Alloc alloc => StoreTemp(state, alloc.Target, EmitAlloc(state, alloc.SizeBytes)),
            IrInst.AddInt addInt => StoreTemp(state, addInt.Target, LlvmApi.BuildAdd(builder, LoadTemp(state, addInt.Left), LoadTemp(state, addInt.Right), $"add_{addInt.Target}")),
            IrInst.AddFloat addFloat => StoreTemp(state, addFloat.Target, LlvmApi.BuildFAdd(builder, LoadTempAsFloat(state, addFloat.Left), LoadTempAsFloat(state, addFloat.Right), $"fadd_{addFloat.Target}")),
            IrInst.SubInt subInt => StoreTemp(state, subInt.Target, LlvmApi.BuildSub(builder, LoadTemp(state, subInt.Left), LoadTemp(state, subInt.Right), $"sub_{subInt.Target}")),
            IrInst.SubFloat subFloat => StoreTemp(state, subFloat.Target, LlvmApi.BuildFSub(builder, LoadTempAsFloat(state, subFloat.Left), LoadTempAsFloat(state, subFloat.Right), $"fsub_{subFloat.Target}")),
            IrInst.MulInt mulInt => StoreTemp(state, mulInt.Target, LlvmApi.BuildMul(builder, LoadTemp(state, mulInt.Left), LoadTemp(state, mulInt.Right), $"mul_{mulInt.Target}")),
            IrInst.MulFloat mulFloat => StoreTemp(state, mulFloat.Target, LlvmApi.BuildFMul(builder, LoadTempAsFloat(state, mulFloat.Left), LoadTempAsFloat(state, mulFloat.Right), $"fmul_{mulFloat.Target}")),
            IrInst.DivInt divInt => StoreTemp(state, divInt.Target, LlvmApi.BuildSDiv(builder, LoadTemp(state, divInt.Left), LoadTemp(state, divInt.Right), $"div_{divInt.Target}")),
            IrInst.DivFloat divFloat => StoreTemp(state, divFloat.Target, LlvmApi.BuildFDiv(builder, LoadTempAsFloat(state, divFloat.Left), LoadTempAsFloat(state, divFloat.Right), $"fdiv_{divFloat.Target}")),
            IrInst.CmpIntGe cmpIntGe => StoreTemp(state, cmpIntGe.Target, EmitIntComparison(state, LlvmIntPredicate.Sge, LoadTemp(state, cmpIntGe.Left), LoadTemp(state, cmpIntGe.Right), $"cmp_ge_{cmpIntGe.Target}")),
            IrInst.CmpFloatGe cmpFloatGe => StoreTemp(state, cmpFloatGe.Target, EmitFloatComparison(state, LlvmRealPredicate.Oge, LoadTempAsFloat(state, cmpFloatGe.Left), LoadTempAsFloat(state, cmpFloatGe.Right), $"fcmp_ge_{cmpFloatGe.Target}")),
            IrInst.CmpIntLe cmpIntLe => StoreTemp(state, cmpIntLe.Target, EmitIntComparison(state, LlvmIntPredicate.Sle, LoadTemp(state, cmpIntLe.Left), LoadTemp(state, cmpIntLe.Right), $"cmp_le_{cmpIntLe.Target}")),
            IrInst.CmpFloatLe cmpFloatLe => StoreTemp(state, cmpFloatLe.Target, EmitFloatComparison(state, LlvmRealPredicate.Ole, LoadTempAsFloat(state, cmpFloatLe.Left), LoadTempAsFloat(state, cmpFloatLe.Right), $"fcmp_le_{cmpFloatLe.Target}")),
            IrInst.CmpIntEq cmpIntEq => StoreTemp(state, cmpIntEq.Target, EmitIntComparison(state, LlvmIntPredicate.Eq, LoadTemp(state, cmpIntEq.Left), LoadTemp(state, cmpIntEq.Right), $"cmp_eq_{cmpIntEq.Target}")),
            IrInst.CmpFloatEq cmpFloatEq => StoreTemp(state, cmpFloatEq.Target, EmitFloatComparison(state, LlvmRealPredicate.Oeq, LoadTempAsFloat(state, cmpFloatEq.Left), LoadTempAsFloat(state, cmpFloatEq.Right), $"fcmp_eq_{cmpFloatEq.Target}")),
            IrInst.CmpIntNe cmpIntNe => StoreTemp(state, cmpIntNe.Target, EmitIntComparison(state, LlvmIntPredicate.Ne, LoadTemp(state, cmpIntNe.Left), LoadTemp(state, cmpIntNe.Right), $"cmp_ne_{cmpIntNe.Target}")),
            IrInst.CmpFloatNe cmpFloatNe => StoreTemp(state, cmpFloatNe.Target, EmitFloatComparison(state, LlvmRealPredicate.One, LoadTempAsFloat(state, cmpFloatNe.Left), LoadTempAsFloat(state, cmpFloatNe.Right), $"fcmp_ne_{cmpFloatNe.Target}")),
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

    private static bool StoreTemp(LlvmCodegenState state, int target, LlvmValueHandle value)
    {
        LlvmApi.BuildStore(state.Target.Builder, NormalizeToI64(state, value), state.TempSlots[target]);
        return false;
    }

    private static bool StoreLocal(LlvmCodegenState state, int slot, LlvmValueHandle value)
    {
        LlvmApi.BuildStore(state.Target.Builder, NormalizeToI64(state, value), state.LocalSlots[slot]);
        return false;
    }

    private static LlvmValueHandle LoadTemp(LlvmCodegenState state, int temp)
    {
        return LlvmApi.BuildLoad2(state.Target.Builder, state.I64, state.TempSlots[temp], $"tmpv_{temp}");
    }

    private static LlvmValueHandle LoadTempAsFloat(LlvmCodegenState state, int temp)
    {
        return LlvmApi.BuildBitCast(state.Target.Builder, LoadTemp(state, temp), state.F64, $"tmpf_{temp}");
    }

    private static LlvmValueHandle NormalizeToI64(LlvmCodegenState state, LlvmValueHandle value)
    {
        return LlvmApi.GetTypeKind(LlvmApi.TypeOf(value)) switch
        {
            LlvmTypeKind.Integer when LlvmApi.GetIntTypeWidth(LlvmApi.TypeOf(value)) == 64 => value,
            LlvmTypeKind.Integer => LlvmApi.BuildZExt(state.Target.Builder, value, state.I64, "zext_i64"),
            LlvmTypeKind.Double => LlvmApi.BuildBitCast(state.Target.Builder, value, state.I64, "f64_i64"),
            LlvmTypeKind.Pointer => LlvmApi.BuildPtrToInt(state.Target.Builder, value, state.I64, "ptr_i64"),
            _ => throw new InvalidOperationException($"Cannot normalize LLVM value of type '{LlvmApi.GetTypeKind(LlvmApi.TypeOf(value))}' to i64.")
        };
    }

    private sealed record LlvmCodegenState(
        LlvmTargetContext Target,
        LlvmValueHandle Function,
        IReadOnlyDictionary<string, string> StringLiterals,
        IReadOnlyDictionary<string, LlvmValueHandle> LiftedFunctions,
        LlvmValueHandle ProgramArgsSlot,
        LlvmValueHandle[] TempSlots,
        LlvmValueHandle[] LocalSlots,
        LlvmValueHandle HeapCursorSlot,
        LlvmValueHandle HeapEndSlot,
        Dictionary<string, LlvmBasicBlockHandle> LabelBlocks,
        Dictionary<int, LlvmBasicBlockHandle> FallthroughBlocks,
        LlvmTypeHandle I64,
        LlvmTypeHandle I32,
        LlvmTypeHandle I8,
        LlvmTypeHandle F64,
        LlvmTypeHandle I8Ptr,
        LlvmTypeHandle I32Ptr,
        LlvmTypeHandle I64Ptr,
        LlvmValueHandle EntryStackPointer,
        LlvmValueHandle WindowsGetStdHandleImport,
        LlvmValueHandle WindowsWriteFileImport,
        LlvmValueHandle WindowsReadFileImport,
        LlvmValueHandle WindowsCreateFileImport,
        LlvmValueHandle WindowsCloseHandleImport,
        LlvmValueHandle WindowsGetFileAttributesImport,
        LlvmValueHandle WindowsWsaStartupImport,
        LlvmValueHandle WindowsSocketImport,
        LlvmValueHandle WindowsConnectImport,
        LlvmValueHandle WindowsSendImport,
        LlvmValueHandle WindowsRecvImport,
        LlvmValueHandle WindowsCloseSocketImport,
        LlvmValueHandle WindowsExitProcessImport,
        LlvmValueHandle WindowsGetCommandLineImport,
        LlvmValueHandle WindowsWideCharToMultiByteImport,
        LlvmValueHandle WindowsLocalFreeImport,
        LlvmValueHandle WindowsCommandLineToArgvImport,
        LlvmValueHandle WindowsSleepImport,
        LlvmValueHandle WindowsVirtualAllocImport,
        LlvmCodegenFlavor Flavor,
        bool UsesProgramArgs,
        bool IsEntry)
    {
        public LlvmBasicBlockHandle GetLabelBlock(string name) => LabelBlocks[name];

        public LlvmBasicBlockHandle GetOrCreateFallthroughBlock(int instructionIndex)
        {
            if (!FallthroughBlocks.TryGetValue(instructionIndex, out LlvmBasicBlockHandle block))
            {
                block = LlvmApi.AppendBasicBlockInContext(Target.Context, Function, $"bb_{instructionIndex}");
                FallthroughBlocks[instructionIndex] = block;
            }

            return block;
        }

        public LlvmBasicBlockHandle GetNextReachableBlock(int instructionIndex)
        {
            int nextIndex = instructionIndex + 1;
            return FallthroughBlocks.TryGetValue(nextIndex, out LlvmBasicBlockHandle block)
                ? block
                : GetOrCreateFallthroughBlock(nextIndex);
        }
    }

    private enum LlvmCodegenFlavor
    {
        LinuxX64,
        LinuxArm64,
        WindowsX64
    }

    private static bool IsLinuxFlavor(LlvmCodegenFlavor flavor) =>
        flavor is LlvmCodegenFlavor.LinuxX64 or LlvmCodegenFlavor.LinuxArm64;

    /// <summary>
    /// Translates x86-64 syscall constants to the correct number for the target architecture.
    /// AArch64 Linux uses different syscall numbers from x86-64.
    /// </summary>
    private static long ResolveSyscallNr(LlvmCodegenFlavor flavor, long x86Nr)
    {
        if (flavor != LlvmCodegenFlavor.LinuxArm64)
        {
            return x86Nr;
        }

        return x86Nr switch
        {
            SyscallRead => Arm64SyscallRead,
            SyscallWrite => Arm64SyscallWrite,
            SyscallOpen => Arm64SyscallOpenat,
            SyscallClose => Arm64SyscallClose,
            SyscallMmap => Arm64SyscallMmap,
            SyscallLseek => Arm64SyscallLseek,
            SyscallSocket => Arm64SyscallSocket,
            SyscallConnect => Arm64SyscallConnect,
            SyscallNanosleep => Arm64SyscallNanosleep,
            SyscallClockGettime => Arm64SyscallClockGettime,
            SyscallExit => Arm64SyscallExit,
            _ => throw new ArgumentOutOfRangeException(nameof(x86Nr), $"No AArch64 mapping for x86-64 syscall {x86Nr}.")
        };
    }
}
