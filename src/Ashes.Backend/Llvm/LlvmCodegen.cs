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
    private const int StdinReadBufSize = 64 * 1024;
    private const int MaxFileReadBytes = 1024 * 1024;
    private const uint Utf8CodePage = 65001;
    private const uint StdOutputHandle = 0xFFFFFFF5;
    private const uint StdInputHandle = 0xFFFFFFF6;
    private const long SyscallRead = 0;
    private const long SyscallWrite = 1;
    private const long SyscallOpen = 2;
    private const long SyscallClose = 3;
    private const long SyscallMmap = 9;
    private const long SyscallMunmap = 11;
    private const long SyscallLseek = 8;
    private const long SyscallSocket = 41;
    private const long SyscallConnect = 42;
    private const long SyscallBind = 49;
    private const long SyscallListen = 50;
    private const long SyscallSetsockopt = 54;
    private const long SyscallAccept4 = 288;
    private const long SyscallFcntl = 72;
    private const long SyscallEpollCtl = 233;
    private const long SyscallEpollWait = 232;
    private const long SyscallEpollCreate1 = 291;
    private const long SyscallNanosleep = 35;
    private const long SyscallDup2 = 33;
    private const long SyscallFork = 57;
    private const long SyscallExecve = 59;
    private const long SyscallWaitpid = 61;
    private const long SyscallKill = 62;
    private const long SyscallPipe2 = 293;
    private const long SyscallClockGettime = 228;
    private const long SyscallExit = 60;
    private const long SyscallGetpid = 39;
    private const long SyscallGetppid = 110;
    private const long SyscallClone = 56;
    private const long SyscallFutex = 202;
    private const long SyscallArchPrctl = 158;
    private const long SyscallPrctl = 157;
    private const long SyscallRtSigaction = 13;
    private const long SyscallIoctl = 16;
    private const long SyscallPpoll = 271;
    private const long SyscallSchedGetaffinity = 204;
    // ARCH_SET_GS, not ARCH_SET_FS: glibc (networking images are dynamically linked) owns the
    // FS base for its thread-local storage on x86-64. GS is unused by userspace on Linux, so
    // pointing it at our per-thread arena control block coexists with libc TLS.
    private const long ArchSetGs = 0x1001;
    // futex op codes (FUTEX_PRIVATE_FLAG = 128)
    private const long FutexWaitPrivate = 0 | 128;
    private const long FutexWakePrivate = 1 | 128;

    // Per-thread control block (TCB). On linux-x64 the GS segment base points at the TCB,
    // giving each thread a private bump arena that coexists with FS-based glibc TLS.
    // To stay clear of the O0 FastISel bug that mis-propagates address-space-256 segment
    // provenance into later stores, code never addresses the arena via an addrspace(256)
    // pointer. Instead offset 0 holds a self-pointer (the TCB base); functions recover the
    // base with a single opaque `movq %gs:0` and then address cursor/end as ordinary
    // pointers. Offsets reserved generously for future per-thread fields.
    private const ulong TcbSelfOffset = 0;
    private const ulong TcbHeapCursorOffset = 8;
    private const ulong TcbHeapEndOffset = 16;
    // Persistent to-space arena cursor/end (see IrInst.AllocAdtToSpace). Lazily initialized (both 0).
    private const ulong TcbToSpaceCursorOffset = 24;
    private const ulong TcbToSpaceEndOffset = 32;
    // Persistent blob region cursor/end: materialized heap leaf fields (Map keys/values) live here, kept
    // separate from the to-space NODE arena so that arena never interleaves fixed-size nodes with
    // variable-size blobs (interleaving corrupts the in-place reuse rebuild). Lazily initialized (both 0).
    private const ulong TcbBlobCursorOffset = 40;
    private const ulong TcbBlobEndOffset = 48;
    private const int MainTcbSizeBytes = 512;
    private const long LinuxErrWouldBlock = -11;
    private const long LinuxErrAlready = -114;
    private const long LinuxErrInProgress = -115;
    private const long LinuxErrIsConnected = -106;
    private const long LinuxFcntlGetFlags = 3;
    private const long LinuxFcntlSetFlags = 4;
    private const long LinuxOpenNonBlocking = 0x800;

    // AArch64 Linux syscall numbers
    private const long Arm64SyscallClose = 57;
    private const long Arm64SyscallOpenat = 56;
    private const long Arm64SyscallLseek = 62;
    private const long Arm64SyscallMmap = 222;
    private const long Arm64SyscallMunmap = 215;
    private const long Arm64SyscallRead = 63;
    private const long Arm64SyscallWrite = 64;
    private const long Arm64SyscallExit = 93;
    private const long Arm64SyscallGetpid = 172;
    private const long Arm64SyscallGetppid = 173;
    private const long Arm64SyscallSocket = 198;
    private const long Arm64SyscallConnect = 203;
    private const long Arm64SyscallBind = 200;
    private const long Arm64SyscallListen = 201;
    private const long Arm64SyscallSetsockopt = 208;
    private const long Arm64SyscallAccept4 = 242;
    private const long Arm64SyscallFcntl = 25;
    private const long Arm64SyscallEpollCreate1 = 20;
    private const long Arm64SyscallEpollCtl = 21;
    private const long Arm64SyscallEpollPwait = 22;
    private const long Arm64SyscallNanosleep = 101;
    private const long Arm64SyscallClockGettime = 113;
    private const long Arm64SyscallDup3 = 24;
    private const long Arm64SyscallClone = 220;
    private const long Arm64SyscallFutex = 98;
    private const long Arm64SyscallExecve = 221;
    private const long Arm64SyscallWait4 = 260;
    private const long Arm64SyscallKill = 129;
    private const long Arm64SyscallPipe2 = 59;
    private const long Arm64SyscallSchedGetaffinity = 123;
    private const long Arm64SyscallPrctl = 167;
    private const long Arm64SyscallRtSigaction = 134;
    private const long Arm64SyscallIoctl = 29;
    private const long Arm64SyscallPpoll = 73;
    private const uint WindowsFionBio = 0x8004667E;
    private const uint WindowsSioGetExtensionFunctionPointer = 0xC8000006;
    private const int WindowsWsaErrorWouldBlock = 10035;
    private const int WindowsWsaErrorInProgress = 10036;
    private const int WindowsWsaErrorAlready = 10037;
    private const int WindowsWsaErrorIsConnected = 10056;
    private const int WindowsErrorIoPending = 997;
    private const ushort WindowsPollReadNormal = 0x0100;
    private const ushort WindowsPollWriteNormal = 0x0010;
    private const int WindowsPollFdSize = 16;

    // Max WSAPoll entries in the detached-task wait (slot 0 = the driving task; the rest are
    // detached socket waits). Overflow tasks are not polled that round — they are stepped again
    // on the next wait — so this caps poll width, not concurrency. The array is a module-global
    // scratch (WindowsPollFdSize * this bytes), so a larger cap only costs static memory.
    private const int DetachedPollFdCapacity = 4096;
    private const int WindowsSolSocket = unchecked((int)0xFFFF);
    private const int WindowsSoUpdateConnectContext = 0x7010;
    private const int TlsVerifyPeer = 0x01;
    private const int TlsCtrlSetSni = 55;
    private const int TlsErrorWantRead = 2;
    private const int TlsErrorWantWrite = 3;
    private const int TlsErrorZeroReturn = 6;
    private const string FileReadFailedMessage = "Ashes.IO.File.readText() failed";
    private const string FileWriteFailedMessage = "Ashes.IO.File.writeText() failed";
    private const string FileReadInvalidUtf8Message = "Ashes.IO.File.readText() encountered invalid UTF-8";
    private const string TextParseIntInvalidMessage = "Ashes.Text.parseInt() invalid input";
    private const string TextParseIntOverflowMessage = "Ashes.Text.parseInt() overflow";
    private const string TextParseFloatInvalidMessage = "Ashes.Text.parseFloat() invalid input";
    private const string TextParseFloatRangeMessage = "Ashes.Text.parseFloat() out of range";
    private const string TcpConnectFailedMessage = "Ashes.Net.Tcp.connect() failed";
    private const string TcpSendFailedMessage = "Ashes.Net.Tcp.send() failed";
    private const string TcpReceiveFailedMessage = "Ashes.Net.Tcp.receive() failed";
    private const string TcpCloseFailedMessage = "Ashes.Net.Tcp.close() failed";
    private const string TcpInvalidUtf8Message = "Ashes.Net.Tcp.receive() encountered invalid UTF-8";
    private const string TcpInvalidMaxBytesMessage = "Ashes.Net.Tcp.receive() maxBytes must be positive";
    private const string TcpResolveFailedMessage = "Ashes.Net.Tcp.connect() could not resolve host";
    private const string TcpListenFailedMessage = "Ashes.Net.Tcp.Server.listen() failed";
    private const string TcpAcceptFailedMessage = "Ashes.Net.Tcp.Server.accept() failed";
    private const string HttpHttpsNotSupportedMessage = "https not supported";
    private const string HttpMalformedUrlMessage = "malformed URL";
    private const string HttpMalformedResponseMessage = "malformed HTTP response";
    private const string HttpUnsupportedTransferEncodingMessage = "unsupported transfer encoding";
    private const int TlsResultOk = 7000;
    private const int TlsResultPlaintextEmpty = 7011;
    private const int MbedTlsWantRead = -0x6900;
    private const int MbedTlsWantWrite = -0x6880;
    private const int MbedTlsPeerCloseNotify = -0x7880;
    private const int MbedTlsSslContextBytes = 768;
    private const int MbedTlsSslConfigBytes = 512;
    private const int MbedTlsEntropyContextBytes = 1024;
    private const int MbedTlsCtrDrbgContextBytes = 512;
    private const int MbedTlsX509CrtBytes = 1024;
    private const int MbedTlsPkContextBytes = 64;
    private const int MbedTlsConnectionSslOffset = 0;
    private const int MbedTlsConnectionWantReadOffset = MbedTlsSslContextBytes;
    private const int MbedTlsConnectionWantWriteOffset = MbedTlsConnectionWantReadOffset + 8;
    private const int MbedTlsConnectionHandshakeDoneOffset = MbedTlsConnectionWantWriteOffset + 8;
    private const int MbedTlsConnectionSocketOffset = MbedTlsConnectionHandshakeDoneOffset + 8;
    private const int MbedTlsConnectionVerifyFlagsOffset = MbedTlsConnectionSocketOffset + 8;
    private const int MbedTlsConnectionTotalBytes = MbedTlsConnectionVerifyFlagsOffset + 8;
    private const int MbedTlsCertifiedKeyCertOffset = 0;
    private const int MbedTlsCertifiedKeyKeyOffset = MbedTlsX509CrtBytes;
    private const int MbedTlsCertifiedKeyTotalBytes = MbedTlsX509CrtBytes + MbedTlsPkContextBytes;
    private const int MbedTlsServerConfigConfigOffset = 0;
    private const int MbedTlsServerConfigKeyOffset = MbedTlsSslConfigBytes;
    private const int MbedTlsServerConfigTotalBytes = MbedTlsServerConfigKeyOffset + 8;
    private const int MbedTlsRuntimeEntropyOffset = 0;
    private const int MbedTlsRuntimeCtrDrbgOffset = MbedTlsRuntimeEntropyOffset + MbedTlsEntropyContextBytes;
    private const int MbedTlsRuntimeRootsOffset = MbedTlsRuntimeCtrDrbgOffset + MbedTlsCtrDrbgContextBytes;
    private const int MbedTlsRuntimeClientConfigOffset = MbedTlsRuntimeRootsOffset + MbedTlsX509CrtBytes;
    private const int MbedTlsRuntimeTotalBytes = MbedTlsRuntimeClientConfigOffset + MbedTlsSslConfigBytes;
    private const string TlsRuntimeInitFailedMessage = "Ashes TLS runtime initialization failed";
    private const string TlsHandshakeFailedMessage = "Ashes TLS handshake failed";
    private const string TlsSendFailedMessage = "Ashes TLS send failed";
    private const string TlsReceiveFailedMessage = "Ashes TLS receive failed";
    private const string TlsCloseFailedMessage = "Ashes TLS close failed";
    private const string TlsInvalidUtf8Message = "Ashes TLS receive() encountered invalid UTF-8";

    private static class WindowsIocpOperationLayout
    {
        internal const int Status = 0;
        internal const int BytesTransferred = 8;
        internal const int Overlapped = 16;
        internal const int TotalSize = 48;

        internal const long StatePending = 0;
        internal const long StateCompleted = 1;
        internal const long StateFailed = 2;
    }

    private static class TlsSessionLayout
    {
        internal const int Socket = 0;
        internal const int SslHandle = 8;
        internal const int TotalSize = 16;
    }

    public static byte[] Compile(IrProgram program, string targetId, BackendCompileOptions options)
    {
        return targetId switch
        {
            Backends.TargetIds.LinuxX64 => CompileLinux(program, options),
            Backends.TargetIds.LinuxArm64 => CompileLinuxArm64(program, options),
            Backends.TargetIds.WindowsX64 => CompileWindows(program, options),
            Backends.TargetIds.WindowsArm64 => CompileWindowsArm64(program, options),
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unknown target '{targetId}'."),
        };
    }

    private static byte[] CompileWindows(IrProgram program, BackendCompileOptions options)
    {
        using LlvmTargetContext target = LlvmTargetSetup.Create(Backends.TargetIds.WindowsX64, options.OptimizationLevel, options.TargetCpu, options.ParallelWorkerStackBytes, options.ParallelWorkerCap);
        var literals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        bool usesTlsRuntime = ProgramUsesTlsRuntimeAbi(program);
        EmitProgramModule(target, program, "entry", LlvmCodegenFlavor.WindowsX64, options, usesTlsRuntime);

        VerifyModule(target);
        RunLlvmOptimizationPasses(target, options.OptimizationLevel);
        // Debug builds combine with any -O level (CO-21). The pre-pass verify above checks the
        // unoptimized module; re-verify after the passes so an inliner-mangled debug location or
        // inlined-at chain is caught here rather than shipped as invalid DWARF. Debug-only, so the
        // hot non-debug compile path is unaffected.
        if (options.EmitDebugInfo)
        {
            VerifyModule(target);
        }
        // Link openlibm AFTER the program's optimization passes so its already-optimized bitcode is
        // not re-optimized (which would re-form libcall intrinsics such as llvm.exp2).
        LinkOpenlibmBitcodeIfNeeded(target, program, Backends.TargetIds.WindowsX64);
        LinkPcre2BitcodeIfNeeded(target, program, Backends.TargetIds.WindowsX64);
        LinkMbedTlsBitcodeIfNeeded(target, program, Backends.TargetIds.WindowsX64);
        byte[] objectBytes = EmitObjectCode(target);
        return LlvmImageLinker.LinkWindowsExecutable(objectBytes, "entry", null, GetExternalLibraries(program));
    }

    private static byte[] CompileWindowsArm64(IrProgram program, BackendCompileOptions options)
    {
        using LlvmTargetContext target = LlvmTargetSetup.Create(Backends.TargetIds.WindowsArm64, options.OptimizationLevel, options.TargetCpu, options.ParallelWorkerStackBytes, options.ParallelWorkerCap);
        var literals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        bool usesTlsRuntime = ProgramUsesTlsRuntimeAbi(program);
        EmitProgramModule(target, program, "entry", LlvmCodegenFlavor.WindowsArm64, options, usesTlsRuntime);

        VerifyModule(target);
        RunLlvmOptimizationPasses(target, options.OptimizationLevel);
        if (options.EmitDebugInfo)
        {
            VerifyModule(target);
        }

        LinkOpenlibmBitcodeIfNeeded(target, program, Backends.TargetIds.WindowsArm64);
        LinkPcre2BitcodeIfNeeded(target, program, Backends.TargetIds.WindowsArm64);
        LinkMbedTlsBitcodeIfNeeded(target, program, Backends.TargetIds.WindowsArm64);
        byte[] objectBytes = EmitObjectCode(target);
        return LlvmImageLinker.LinkWindowsArm64Executable(objectBytes, "entry", null, GetExternalLibraries(program));
    }

    private static byte[] CompileLinux(IrProgram program, BackendCompileOptions options)
    {
        using LlvmTargetContext target = LlvmTargetSetup.Create(Backends.TargetIds.LinuxX64, options.OptimizationLevel, options.TargetCpu, options.ParallelWorkerStackBytes, options.ParallelWorkerCap);
        var literals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        bool usesTlsRuntime = ProgramUsesTlsRuntimeAbi(program);
        EmitProgramModule(target, program, "entry", LlvmCodegenFlavor.LinuxX64, options, usesTlsRuntime);

        VerifyModule(target);
        RunLlvmOptimizationPasses(target, options.OptimizationLevel);
        // Debug builds combine with any -O level (CO-21). The pre-pass verify above checks the
        // unoptimized module; re-verify after the passes so an inliner-mangled debug location or
        // inlined-at chain is caught here rather than shipped as invalid DWARF. Debug-only, so the
        // hot non-debug compile path is unaffected.
        if (options.EmitDebugInfo)
        {
            VerifyModule(target);
        }
        // Link openlibm AFTER the program's optimization passes so its already-optimized bitcode is
        // not re-optimized (which would re-form libcall intrinsics such as llvm.exp2).
        LinkOpenlibmBitcodeIfNeeded(target, program, Backends.TargetIds.LinuxX64);
        LinkPcre2BitcodeIfNeeded(target, program, Backends.TargetIds.LinuxX64);
        LinkMbedTlsBitcodeIfNeeded(target, program, Backends.TargetIds.LinuxX64);
        byte[] objectBytes = EmitObjectCode(target);
        return LlvmImageLinker.LinkLinuxExecutable(objectBytes, "entry", null, GetExternalLibraries(program));
    }

    private static byte[] CompileLinuxArm64(IrProgram program, BackendCompileOptions options)
    {
        using LlvmTargetContext target = LlvmTargetSetup.Create(Backends.TargetIds.LinuxArm64, options.OptimizationLevel, options.TargetCpu, options.ParallelWorkerStackBytes, options.ParallelWorkerCap);
        var literals = program.StringLiterals.ToDictionary(static literal => literal.Label, static literal => literal.Value, StringComparer.Ordinal);
        bool usesTlsRuntime = ProgramUsesTlsRuntimeAbi(program);
        EmitProgramModule(target, program, "entry", LlvmCodegenFlavor.LinuxArm64, options, usesTlsRuntime);

        VerifyModule(target);
        RunLlvmOptimizationPasses(target, options.OptimizationLevel);
        // Debug builds combine with any -O level (CO-21). The pre-pass verify above checks the
        // unoptimized module; re-verify after the passes so an inliner-mangled debug location or
        // inlined-at chain is caught here rather than shipped as invalid DWARF. Debug-only, so the
        // hot non-debug compile path is unaffected.
        if (options.EmitDebugInfo)
        {
            VerifyModule(target);
        }
        // Link openlibm AFTER the program's optimization passes so its already-optimized bitcode is
        // not re-optimized (which would re-form libcall intrinsics such as llvm.exp2).
        LinkOpenlibmBitcodeIfNeeded(target, program, Backends.TargetIds.LinuxArm64);
        LinkPcre2BitcodeIfNeeded(target, program, Backends.TargetIds.LinuxArm64);
        LinkMbedTlsBitcodeIfNeeded(target, program, Backends.TargetIds.LinuxArm64);
        byte[] objectBytes = EmitObjectCode(target);
        return LlvmImageLinker.LinkLinuxArm64Executable(objectBytes, "entry", null, GetExternalLibraries(program));
    }

    private static void VerifyModule(LlvmTargetContext target)
    {
        int verifyErr = LlvmApi.VerifyModule(target.Module, LlvmVerifierFailureAction.ReturnStatus, out nint verifyMsg);
        if (verifyErr != 0)
        {
            string verifyError = Marshal.PtrToStringAnsi(verifyMsg) ?? "unknown error";
            LlvmApi.DisposeMessage(verifyMsg);
            if (Environment.GetEnvironmentVariable("ASH_DBG_DUMP_IR") is not null)
            {
                nint irPtr = LlvmApi.PrintModuleToString(target.Module);
                System.IO.File.WriteAllText("/tmp/ashes_bad_ir.ll", Marshal.PtrToStringAnsi(irPtr) ?? "");
                LlvmApi.DisposeMessage(irPtr);
            }

            throw new InvalidOperationException($"LLVM module verification failed: {verifyError}");
        }

        if (verifyMsg != 0)
        {
            LlvmApi.DisposeMessage(verifyMsg);
        }
    }

    private static IReadOnlyDictionary<string, string> GetExternalLibraries(IrProgram program)
    {
        return program.ExternalFunctions
            .Where(static f => !string.IsNullOrWhiteSpace(f.LibraryName))
            .ToDictionary(static f => f.SymbolName, static f => f.LibraryName!, StringComparer.Ordinal);
    }

    /// <summary>
    /// Runs a targeted LLVM new pass manager pipeline on the module.
    /// Uses a custom pass string (not <c>default&lt;ON&gt;</c>) to avoid
    /// aggressive transforms that miscompile freestanding inline-assembly
    /// code — in particular <c>simplifycfg</c> (merges blocks across
    /// inline-asm boundaries), loop vectorization, and loop unrolling.
    /// O1: mem2reg, dce, early-cse.
    /// O2: adds reassociate, instcombine, gvn, inline.
    /// O3: adds licm (via loop-mssa), dse.
    /// At O0 no passes are run — codegen output is used as-is.
    /// </summary>
    internal static void RunLlvmOptimizationPasses(LlvmTargetContext target, Backends.BackendOptimizationLevel level)
    {
        if (level == Backends.BackendOptimizationLevel.O0)
        {
            return; // No optimization at O0
        }

        // Use a targeted pass pipeline instead of "default<ON>" to avoid
        // aggressive transforms (loop unrolling, vectorization, lib-call
        // recognition, simplifycfg) that can miscompile freestanding
        // inline-assembly code. Each level adds progressively more passes.
        //
        // Excluded passes:
        //   simplifycfg – merges blocks across inline-asm boundaries → SIGSEGV
        //   loop-vectorize / slp-vectorize – introduces libc vector calls
        //   loop-unroll – code-size explosion for marginal gain
        string passString = level switch
        {
            Backends.BackendOptimizationLevel.O1 =>
                "function(mem2reg,dce,early-cse)",
            Backends.BackendOptimizationLevel.O2 =>
                "function(mem2reg,dce,early-cse,reassociate,instcombine<no-verify-fixpoint>,gvn)" +
                ",cgscc(inline)" +
                // Second inline round: the first round inlines a curried wrapper whose body
                // builds-and-returns a closure; gvn then forwards the stored code pointer into
                // the caller's indirect call, instcombine turns it into a direct call, and only
                // then can that callee be inlined. One extra round collapses these chains.
                ",function(dce,instcombine<no-verify-fixpoint>,gvn,instcombine<no-verify-fixpoint>)" +
                ",cgscc(inline)" +
                ",function(dce,instcombine<no-verify-fixpoint>)",
            Backends.BackendOptimizationLevel.O3 =>
                "function(mem2reg,dce,early-cse,reassociate,instcombine<no-verify-fixpoint>,gvn,loop-mssa(licm))" +
                ",cgscc(inline)" +
                ",function(dce,instcombine<no-verify-fixpoint>,gvn,instcombine<no-verify-fixpoint>)" +
                ",cgscc(inline)" +
                ",function(dce,instcombine<no-verify-fixpoint>,gvn,dse)",
            _ =>
                "function(mem2reg,dce,early-cse,reassociate,instcombine<no-verify-fixpoint>,gvn)" +
                ",cgscc(inline)" +
                ",function(dce,instcombine<no-verify-fixpoint>,gvn,instcombine<no-verify-fixpoint>)" +
                ",cgscc(inline)" +
                ",function(dce,instcombine<no-verify-fixpoint>)",
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

    /// <summary>
    /// Marker consumed by EmitBytesIndexOf: when the TLS runtime already makes this image
    /// dynamically linked against glibc, Byte.indexOf may use glibc's SIMD memchr for free;
    /// otherwise it emits the freestanding SWAR scan so the image stays fully static.
    /// </summary>
    private static void EmitGlibcRuntimeMarker(LlvmTargetContext target, LlvmCodegenFlavor flavor, bool usesTlsRuntime, LlvmTypeHandle i8)
    {
        if (!usesTlsRuntime || IsWindowsFlavor(flavor))
        {
            return;
        }

        LlvmValueHandle glibcMarker = LlvmApi.AddGlobal(target.Module, i8, "__ashes_glibc_runtime");
        LlvmApi.SetInitializer(glibcMarker, LlvmApi.ConstInt(i8, 1, 0));
        LlvmApi.SetLinkage(glibcMarker, LlvmLinkage.Internal);
    }

    private static void EmitProgramModule(
        LlvmTargetContext target,
        IrProgram program,
        string entryFunctionName,
        LlvmCodegenFlavor flavor,
        Backends.BackendCompileOptions options,
        bool usesTlsRuntime)
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

        EmitProgramModuleFlags flags = EmitProgramModuleComputeFlags(program, flavor);
        EmitProgramModuleArena arena = EmitProgramModuleArenaGlobals(target, program, flavor, i64, flags.Arm64UsesTlsArena);

        var imports = new EmitProgramModuleImports();
        EmitProgramModuleImportsStdio(target, flavor, flags, i32, i8Ptr, i32Ptr, i64, imports);
        EmitProgramModuleImportsSocketsA(target, flavor, flags, i8Ptr, i32, i64, i64Ptr, imports);
        EmitProgramModuleImportsSocketsB(target, flavor, flags, i64, imports);
        EmitProgramModuleImportsCert(target, flavor, flags, imports);
        EmitProgramModuleImportsMiscA(target, flavor, flags, voidType, i32, imports);
        EmitProgramModuleImportsMiscB(target, flavor, flags, i32, i8Ptr, i32Ptr, imports);

        // Emit a local memcpy implementation for the freestanding target.
        // LLVM may lower llvm.memcpy intrinsics to calls to memcpy when the size is
        // not a compile-time constant. Since we have no libc, we provide our own.
        EmitBuiltinMemcpy(target, i8, i64, i8Ptr);
        EmitBuiltinMemcmp(target, i8, i64, i8Ptr);
        EmitBuiltinBcmp(target, i8, i64, i8Ptr);

        EmitGlibcRuntimeMarker(target, flavor, usesTlsRuntime, i8);

        // Emit the Ashes.Number.BigInt arbitrary-precision runtime helpers as LLVM IR (like the
        // freestanding memcmp/strlen helpers) when the program uses BigInt.
        if (ProgramUsesBigIntRuntimeAbi(program))
        {
            EmitBigIntRuntimeHelpers(target);
        }

        // Emit the malloc/free the linked PCRE2 payload calls (a bump allocator over the lazily
        // OS-allocated regex region) when the program uses Ashes.Text.Regex.
        if (ProgramUsesRegexRuntimeAbi(program))
        {
            EmitPcre2Allocator(target, i64, i8Ptr);
        }

        // Apply nounwind to all Ashes-defined runtime helpers as well as the entry
        // point and lifted closures. The current runtime ABI layer does not unwind.
        uint nounwindKind = LlvmApi.GetEnumAttributeKindForName("nounwind");
        LlvmAttributeHandle nounwindAttr = LlvmApi.CreateEnumAttribute(target.Context, nounwindKind, 0);

        EmitProgramModuleWorkerCap(target, flavor, flags, nounwindAttr);
        EmitProgramModuleNetworking(target, flavor, flags, arena, imports, i32, i32Ptr, usesTlsRuntime, nounwindAttr);
        EmitProgramModuleParallel(target, program, flavor, flags, nounwindAttr);
        EmitProgramModuleFunctions(target, program, entryFunctionName, flavor, options, flags, arena, imports, i32, i32Ptr, i64, voidType, closureFunctionType, stringLiterals, nounwindAttr);
    }

    private readonly record struct EmitProgramModuleFlags(
        bool UsesProgramArgs,
        bool UsesWindowsStdout,
        bool UsesWindowsExitProcess,
        bool UsesWindowsProgramArgs,
        bool UsesWindowsReadLine,
        bool UsesWindowsFileOps,
        bool UsesNetworkingRuntimeAbi,
        bool UseRunQueueScheduler,
        bool Arm64UsesTlsArena,
        bool UsesWindowsSockets,
        bool UsesWindowsSleep,
        bool UsesWindowsProcess,
        bool UsesWindowsReadExact,
        bool UsesWindowsConsole);

    private readonly record struct EmitProgramModuleArena(
        LlvmValueHandle HeapCursorGlobal,
        LlvmValueHandle HeapEndGlobal,
        LlvmValueHandle ToSpaceCursorGlobal,
        LlvmValueHandle ToSpaceEndGlobal);

    private sealed class EmitProgramModuleImports
    {
        public LlvmValueHandle WindowsGetStdHandleImport { get; set; }
        public LlvmValueHandle WindowsWriteFileImport { get; set; }
        public LlvmValueHandle WindowsReadFileImport { get; set; }
        public LlvmValueHandle WindowsCreateFileImport { get; set; }
        public LlvmValueHandle WindowsCloseHandleImport { get; set; }
        public LlvmValueHandle WindowsGetFileAttributesImport { get; set; }
        public LlvmValueHandle WindowsWsaStartupImport { get; set; }
        public LlvmValueHandle WindowsSocketImport { get; set; }
        public LlvmValueHandle WindowsConnectImport { get; set; }
        public LlvmValueHandle WindowsSendImport { get; set; }
        public LlvmValueHandle WindowsRecvImport { get; set; }
        public LlvmValueHandle WindowsCloseSocketImport { get; set; }
        public LlvmValueHandle WindowsIoctlSocketImport { get; set; }
        public LlvmValueHandle WindowsWsaGetLastErrorImport { get; set; }
        public LlvmValueHandle WindowsWsaPollImport { get; set; }
        public LlvmValueHandle WindowsLoadLibraryImport { get; set; }
        public LlvmValueHandle WindowsGetProcAddressImport { get; set; }
        public LlvmValueHandle WindowsCertOpenSystemStoreImport { get; set; }
        public LlvmValueHandle WindowsCertEnumCertificatesInStoreImport { get; set; }
        public LlvmValueHandle WindowsCertCloseStoreImport { get; set; }
        public LlvmValueHandle WindowsBindImport { get; set; }
        public LlvmValueHandle WindowsSetSockOptImport { get; set; }
        public LlvmValueHandle WindowsWsaIoctlImport { get; set; }
        public LlvmValueHandle WindowsWsaSendImport { get; set; }
        public LlvmValueHandle WindowsWsaRecvImport { get; set; }
        public LlvmValueHandle WindowsCreateIoCompletionPortImport { get; set; }
        public LlvmValueHandle WindowsGetQueuedCompletionStatusImport { get; set; }
        public LlvmValueHandle WindowsIocpPortGlobal { get; set; }
        public LlvmValueHandle WindowsExitProcessImport { get; set; }
        public LlvmValueHandle WindowsGetCommandLineImport { get; set; }
        public LlvmValueHandle WindowsWideCharToMultiByteImport { get; set; }
        public LlvmValueHandle WindowsLocalFreeImport { get; set; }
        public LlvmValueHandle WindowsCommandLineToArgvImport { get; set; }
        public LlvmValueHandle WindowsSleepImport { get; set; }
        public LlvmValueHandle WindowsVirtualAllocImport { get; set; }
        public LlvmValueHandle WindowsVirtualFreeImport { get; set; }
        public LlvmValueHandle WindowsCreatePipeImport { get; set; }
        public LlvmValueHandle WindowsCreateProcessAImport { get; set; }
        public LlvmValueHandle WindowsTerminateProcessImport { get; set; }
        public LlvmValueHandle WindowsWaitForSingleObjectImport { get; set; }
        public LlvmValueHandle WindowsGetExitCodeProcessImport { get; set; }
    }

    private static EmitProgramModuleFlags EmitProgramModuleComputeFlags(IrProgram program, LlvmCodegenFlavor flavor)
    {
        bool isWindows = IsWindowsFlavor(flavor);
        bool usesProgramArgs = ProgramUsesInstruction<IrInst.LoadProgramArgs>(program);
        bool usesReadLine = ProgramUsesInstruction<IrInst.ReadLine>(program);
        bool usesWindowsStdout = isWindows
            && (ProgramUsesInstruction<IrInst.PrintInt>(program)
                || ProgramUsesInstruction<IrInst.PrintStr>(program)
                || ProgramUsesInstruction<IrInst.WriteStr>(program)
                || ProgramUsesInstruction<IrInst.PrintBool>(program)
                || ProgramUsesInstruction<IrInst.PanicStr>(program)
                || usesReadLine);
        bool usesWindowsFileOps = isWindows && EmitProgramModuleUsesFileOps(program);
        bool usesNetworkingRuntimeAbi = EmitProgramModuleUsesNetworking(program);
        bool useRunQueueScheduler = ProgramUsesInstruction<IrInst.RunTask>(program);
        bool arm64UsesTlsArena = flavor == LlvmCodegenFlavor.LinuxArm64;
        bool usesWindowsSockets = isWindows && usesNetworkingRuntimeAbi;
        bool usesWindowsSleep = isWindows
            && (ProgramUsesInstruction<IrInst.AsyncSleep>(program)
                || ProgramUsesInstruction<IrInst.RunTask>(program)
                || ProgramUsesInstruction<IrInst.AsyncAll>(program)
                || ProgramUsesInstruction<IrInst.AsyncRace>(program)
                || usesNetworkingRuntimeAbi);
        bool usesWindowsProcess = isWindows && EmitProgramModuleUsesProcess(program);
        bool usesWindowsReadExact = isWindows && ProgramUsesInstruction<IrInst.ReadExact>(program);
        bool usesWindowsConsole = isWindows
            && (ProgramUsesInstruction<IrInst.ConsoleEnableRaw>(program)
                || ProgramUsesInstruction<IrInst.ConsoleRestore>(program)
                || ProgramUsesInstruction<IrInst.ConsolePoll>(program)
                || ProgramUsesInstruction<IrInst.MonotonicMillis>(program));
        return new EmitProgramModuleFlags(
            usesProgramArgs, usesWindowsStdout, isWindows, isWindows && usesProgramArgs,
            isWindows && usesReadLine, usesWindowsFileOps, usesNetworkingRuntimeAbi, useRunQueueScheduler,
            arm64UsesTlsArena, usesWindowsSockets, usesWindowsSleep, usesWindowsProcess, usesWindowsReadExact,
            usesWindowsConsole);
    }

    private static bool EmitProgramModuleUsesFileOps(IrProgram program)
    {
        return ProgramUsesInstruction<IrInst.FileReadText>(program)
                || ProgramUsesInstruction<IrInst.FileReadAllBytes>(program)
                || ProgramUsesInstruction<IrInst.FileMmap>(program)
                || ProgramUsesInstruction<IrInst.FileWriteText>(program)
                || ProgramUsesInstruction<IrInst.FileExists>(program)
                || ProgramUsesInstruction<IrInst.FileWriteBytes>(program)
                || ProgramUsesInstruction<IrInst.FileOpen>(program)
                || ProgramUsesInstruction<IrInst.FileReadChunk>(program)
                || ProgramUsesInstruction<IrInst.FileReadLine>(program)
                || ProgramUsesInstruction<IrInst.FileClose>(program)
                || ProgramUsesInstruction<IrInst.CleanupResource>(program);
    }

    private static bool EmitProgramModuleUsesNetworking(IrProgram program)
    {
        return ProgramUsesInstruction<IrInst.HttpGet>(program)
                || ProgramUsesInstruction<IrInst.HttpPost>(program)
                || ProgramUsesInstruction<IrInst.NetTcpConnect>(program)
                || ProgramUsesInstruction<IrInst.NetTcpSend>(program)
                || ProgramUsesInstruction<IrInst.NetTcpReceive>(program)
                || ProgramUsesInstruction<IrInst.NetTcpClose>(program)
                || ProgramUsesInstruction<IrInst.NetTcpListen>(program)
                || ProgramUsesInstruction<IrInst.NetTcpAccept>(program)
                || ProgramUsesInstruction<IrInst.CreateTcpConnectTask>(program)
                || ProgramUsesInstruction<IrInst.CreateTcpSendTask>(program)
                || ProgramUsesInstruction<IrInst.CreateTcpReceiveTask>(program)
                || ProgramUsesInstruction<IrInst.CreateTcpCloseTask>(program)
                || ProgramUsesInstruction<IrInst.CreateTcpListenTask>(program)
                || ProgramUsesInstruction<IrInst.CreateForkWorkersTask>(program)
                || ProgramUsesInstruction<IrInst.CreateTcpAcceptTask>(program)
                || ProgramUsesInstruction<IrInst.CreateHttpGetTask>(program)
                || ProgramUsesInstruction<IrInst.CreateHttpPostTask>(program)
                || ProgramUsesInstruction<IrInst.CreateTlsConnectTask>(program)
                || ProgramUsesInstruction<IrInst.CreateTlsHandshakeTask>(program)
                || ProgramUsesInstruction<IrInst.CreateTlsServerHandshakeTask>(program)
                || ProgramUsesInstruction<IrInst.CreateTlsSendTask>(program)
                || ProgramUsesInstruction<IrInst.CreateTlsReceiveTask>(program)
                || ProgramUsesInstruction<IrInst.CreateTlsCloseTask>(program)
                || ProgramUsesInstruction<IrInst.RunTask>(program)
                || ProgramUsesInstruction<IrInst.SpawnTask>(program)
                || ProgramUsesInstruction<IrInst.AsyncSleep>(program)
                || ProgramUsesInstruction<IrInst.AsyncAll>(program)
                || ProgramUsesInstruction<IrInst.AsyncRace>(program)
                || ProgramUsesInstruction<IrInst.CleanupResource>(program);
    }

    private static bool EmitProgramModuleUsesProcess(IrProgram program)
    {
        return ProgramUsesInstruction<IrInst.SpawnProcess>(program)
                || ProgramUsesInstruction<IrInst.ProcessWriteStdin>(program)
                || ProgramUsesInstruction<IrInst.ProcessReadStdoutLine>(program)
                || ProgramUsesInstruction<IrInst.ProcessReadStderrLine>(program)
                || ProgramUsesInstruction<IrInst.ProcessWaitForExit>(program)
                || ProgramUsesInstruction<IrInst.ProcessKill>(program);
    }

    private static EmitProgramModuleArena EmitProgramModuleArenaGlobals(
        LlvmTargetContext target, IrProgram program, LlvmCodegenFlavor flavor, LlvmTypeHandle i64, bool arm64UsesTlsArena)
    {
        // Bump-arena cursor/end. On linux-x64 these live in a per-thread control block
        // reached through the GS segment base, so each worker thread gets its own arena with
        // no shared state and no atomics; the actual cursor/end pointers are built per
        // function from the TCB base (see the LinuxX64 arena setup in EmitFunctionBody), so
        // no module global is needed there. Other flavors keep plain module globals
        // (single-threaded today).
        LlvmValueHandle heapCursorGlobal = default;
        LlvmValueHandle heapEndGlobal = default;
        LlvmValueHandle toSpaceCursorGlobal = default;
        LlvmValueHandle toSpaceEndGlobal = default;
        if (flavor != LlvmCodegenFlavor.LinuxX64)
        {
            heapCursorGlobal = LlvmApi.AddGlobal(target.Module, i64, "__ashes_heap_cursor");
            LlvmApi.SetLinkage(heapCursorGlobal, LlvmLinkage.Internal);
            LlvmApi.SetInitializer(heapCursorGlobal, LlvmApi.ConstInt(i64, 0, 0));
            heapEndGlobal = LlvmApi.AddGlobal(target.Module, i64, "__ashes_heap_end");
            LlvmApi.SetLinkage(heapEndGlobal, LlvmLinkage.Internal);
            LlvmApi.SetInitializer(heapEndGlobal, LlvmApi.ConstInt(i64, 0, 0));
            // Persistent to-space arena (AllocAdtToSpace). Lazily initialized: both start at 0.
            toSpaceCursorGlobal = LlvmApi.AddGlobal(target.Module, i64, "__ashes_tospace_cursor");
            LlvmApi.SetLinkage(toSpaceCursorGlobal, LlvmLinkage.Internal);
            LlvmApi.SetInitializer(toSpaceCursorGlobal, LlvmApi.ConstInt(i64, 0, 0));
            toSpaceEndGlobal = LlvmApi.AddGlobal(target.Module, i64, "__ashes_tospace_end");
            LlvmApi.SetLinkage(toSpaceEndGlobal, LlvmLinkage.Internal);
            LlvmApi.SetInitializer(toSpaceEndGlobal, LlvmApi.ConstInt(i64, 0, 0));
            // Persistent blob region (materialized Map key/value leaf fields). Lazily initialized: both 0.
            LlvmValueHandle blobCursorGlobal = LlvmApi.AddGlobal(target.Module, i64, "__ashes_blob_cursor");
            LlvmApi.SetLinkage(blobCursorGlobal, LlvmLinkage.Internal);
            LlvmApi.SetInitializer(blobCursorGlobal, LlvmApi.ConstInt(i64, 0, 0));
            LlvmValueHandle blobEndGlobal = LlvmApi.AddGlobal(target.Module, i64, "__ashes_blob_end");
            LlvmApi.SetLinkage(blobEndGlobal, LlvmLinkage.Internal);
            LlvmApi.SetInitializer(blobEndGlobal, LlvmApi.ConstInt(i64, 0, 0));

            // arm64 has no spare thread register (linux-x64 uses GS, win-x64 uses the TEB), so its
            // per-thread arena is real ELF TLS: mark the six arena cursors thread-local and LLVM
            // (static reloc → local-exec) emits the mrs tpidr_el0 + TPREL sequence the ELF linker
            // resolves. Enabled for every arm64 image — including dynamically linked (networking /
            // external) ones, whose loader reserves this image's local-exec PT_TLS in the static-TLS
            // block independently of the loader's own DTV-backed dynamic TLS.
            // win-x64 keeps these as ordinary globals (overridden by the TEB-TCB slots).
            if (arm64UsesTlsArena)
            {
                foreach (var g in new[] { heapCursorGlobal, heapEndGlobal, toSpaceCursorGlobal, toSpaceEndGlobal, blobCursorGlobal, blobEndGlobal })
                {
                    LlvmApi.SetThreadLocal(g, 1);
                }
            }
        }
        // Dynamically-scoped handler-evidence slots: one module global per declared capability, holding
        // the innermost installed handler frame pointer (0 = none). Plain globals on every flavor —
        // capabilities interacting with structured parallelism is out of scope for the current stage.
        for (int capabilityIndex = 0; capabilityIndex < program.CapabilityHandlerGlobals; capabilityIndex++)
        {
            LlvmValueHandle capabilityGlobal = LlvmApi.AddGlobal(target.Module, i64, $"__ashes_capability_handler_{capabilityIndex}");
            LlvmApi.SetLinkage(capabilityGlobal, LlvmLinkage.Internal);
            LlvmApi.SetInitializer(capabilityGlobal, LlvmApi.ConstInt(i64, 0, 0));
        }
        return new EmitProgramModuleArena(heapCursorGlobal, heapEndGlobal, toSpaceCursorGlobal, toSpaceEndGlobal);
    }

    private static void EmitProgramModuleImportsStdio(
        LlvmTargetContext target, LlvmCodegenFlavor flavor, EmitProgramModuleFlags flags,
        LlvmTypeHandle i32, LlvmTypeHandle i8Ptr, LlvmTypeHandle i32Ptr, LlvmTypeHandle i64, EmitProgramModuleImports imports)
    {
        if (flags.UsesWindowsStdout || flags.UsesWindowsReadLine || flags.UsesWindowsReadExact || flags.UsesWindowsConsole)
        {
            LlvmTypeHandle getStdHandleType = LlvmApi.FunctionType(i64, [i32]);
            imports.WindowsGetStdHandleImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_GetStdHandle");
            LlvmApi.SetLinkage(imports.WindowsGetStdHandleImport, LlvmLinkage.External);
        }

        if (flags.UsesWindowsStdout || flags.UsesWindowsFileOps || flags.UsesNetworkingRuntimeAbi || flags.UsesWindowsProcess)
        {
            LlvmTypeHandle writeFileType = LlvmApi.FunctionType(i32, [i64, i8Ptr, i32, i32Ptr, i8Ptr]);
            imports.WindowsWriteFileImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_WriteFile");
            LlvmApi.SetLinkage(imports.WindowsWriteFileImport, LlvmLinkage.External);
        }

        if (flags.UsesWindowsReadLine || flags.UsesWindowsFileOps || flags.UsesWindowsReadExact || flags.UsesWindowsProcess || flags.UsesWindowsConsole)
        {
            LlvmTypeHandle readFileType = LlvmApi.FunctionType(i32, [i64, i8Ptr, i32, i32Ptr, i8Ptr]);
            imports.WindowsReadFileImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_ReadFile");
            LlvmApi.SetLinkage(imports.WindowsReadFileImport, LlvmLinkage.External);
        }

        if (flags.UsesWindowsFileOps || flags.UsesWindowsSockets || flags.UsesWindowsProcess)
        {
            imports.WindowsCloseHandleImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_CloseHandle");
            LlvmApi.SetLinkage(imports.WindowsCloseHandleImport, LlvmLinkage.External);
        }

        if (flags.UsesWindowsFileOps || flags.UsesNetworkingRuntimeAbi)
        {
            LlvmTypeHandle createFileType = LlvmApi.FunctionType(i64, [i8Ptr, i32, i32, i8Ptr, i32, i32, i64]);
            LlvmTypeHandle getFileAttributesType = LlvmApi.FunctionType(i32, [i8Ptr]);
            imports.WindowsCreateFileImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_CreateFileA");
            LlvmApi.SetLinkage(imports.WindowsCreateFileImport, LlvmLinkage.External);
            imports.WindowsGetFileAttributesImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_GetFileAttributesA");
            LlvmApi.SetLinkage(imports.WindowsGetFileAttributesImport, LlvmLinkage.External);
        }
    }

    private static void EmitProgramModuleImportsSocketsA(
        LlvmTargetContext target, LlvmCodegenFlavor flavor, EmitProgramModuleFlags flags,
        LlvmTypeHandle i8Ptr, LlvmTypeHandle i32, LlvmTypeHandle i64, LlvmTypeHandle i64Ptr, EmitProgramModuleImports imports)
    {
        if (flags.UsesWindowsSockets)
        {
            LlvmTypeHandle wsaStartupType = LlvmApi.FunctionType(i32, [LlvmApi.Int16TypeInContext(target.Context), i8Ptr]);
            LlvmTypeHandle socketType = LlvmApi.FunctionType(i64, [i32, i32, i32]);
            LlvmTypeHandle connectType = LlvmApi.FunctionType(i32, [i64, i8Ptr, i32]);
            LlvmTypeHandle sendType = LlvmApi.FunctionType(i32, [i64, i8Ptr, i32, i32]);
            LlvmTypeHandle recvType = LlvmApi.FunctionType(i32, [i64, i8Ptr, i32, i32]);
            LlvmTypeHandle closeSocketType = LlvmApi.FunctionType(i32, [i64]);
            LlvmTypeHandle ioctlSocketType = LlvmApi.FunctionType(i32, [i64, i32, i64Ptr]);
            LlvmTypeHandle wsaGetLastErrorType = LlvmApi.FunctionType(i32, []);
            LlvmTypeHandle wsaPollType = LlvmApi.FunctionType(i32, [i8Ptr, i32, i32]);
            imports.WindowsWsaStartupImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_WSAStartup");
            LlvmApi.SetLinkage(imports.WindowsWsaStartupImport, LlvmLinkage.External);
            imports.WindowsSocketImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_socket");
            LlvmApi.SetLinkage(imports.WindowsSocketImport, LlvmLinkage.External);
            imports.WindowsConnectImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_connect");
            LlvmApi.SetLinkage(imports.WindowsConnectImport, LlvmLinkage.External);
            imports.WindowsSendImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_send");
            LlvmApi.SetLinkage(imports.WindowsSendImport, LlvmLinkage.External);
            imports.WindowsRecvImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_recv");
            LlvmApi.SetLinkage(imports.WindowsRecvImport, LlvmLinkage.External);
            imports.WindowsCloseSocketImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_closesocket");
            LlvmApi.SetLinkage(imports.WindowsCloseSocketImport, LlvmLinkage.External);
            imports.WindowsIoctlSocketImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_ioctlsocket");
            LlvmApi.SetLinkage(imports.WindowsIoctlSocketImport, LlvmLinkage.External);
            imports.WindowsWsaGetLastErrorImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_WSAGetLastError");
            LlvmApi.SetLinkage(imports.WindowsWsaGetLastErrorImport, LlvmLinkage.External);
            imports.WindowsWsaPollImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_WSAPoll");
            LlvmApi.SetLinkage(imports.WindowsWsaPollImport, LlvmLinkage.External);
        }
    }

    private static void EmitProgramModuleImportsSocketsB(
        LlvmTargetContext target, LlvmCodegenFlavor flavor, EmitProgramModuleFlags flags, LlvmTypeHandle i64, EmitProgramModuleImports imports)
    {
        if (flags.UsesWindowsSockets)
        {
            LlvmValueHandle windowsListenImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_listen");
            LlvmApi.SetLinkage(windowsListenImport, LlvmLinkage.External);
            LlvmValueHandle windowsAcceptImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_accept");
            LlvmApi.SetLinkage(windowsAcceptImport, LlvmLinkage.External);
            imports.WindowsLoadLibraryImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_LoadLibraryA");
            LlvmApi.SetLinkage(imports.WindowsLoadLibraryImport, LlvmLinkage.External);
            imports.WindowsGetProcAddressImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_GetProcAddress");
            LlvmApi.SetLinkage(imports.WindowsGetProcAddressImport, LlvmLinkage.External);
            imports.WindowsBindImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_bind");
            LlvmApi.SetLinkage(imports.WindowsBindImport, LlvmLinkage.External);
            imports.WindowsSetSockOptImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_setsockopt");
            LlvmApi.SetLinkage(imports.WindowsSetSockOptImport, LlvmLinkage.External);
            imports.WindowsWsaIoctlImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_WSAIoctl");
            LlvmApi.SetLinkage(imports.WindowsWsaIoctlImport, LlvmLinkage.External);
            imports.WindowsWsaSendImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_WSASend");
            LlvmApi.SetLinkage(imports.WindowsWsaSendImport, LlvmLinkage.External);
            imports.WindowsWsaRecvImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_WSARecv");
            LlvmApi.SetLinkage(imports.WindowsWsaRecvImport, LlvmLinkage.External);
            imports.WindowsCreateIoCompletionPortImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_CreateIoCompletionPort");
            LlvmApi.SetLinkage(imports.WindowsCreateIoCompletionPortImport, LlvmLinkage.External);
            imports.WindowsGetQueuedCompletionStatusImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_GetQueuedCompletionStatus");
            LlvmApi.SetLinkage(imports.WindowsGetQueuedCompletionStatusImport, LlvmLinkage.External);
            imports.WindowsIocpPortGlobal = LlvmApi.AddGlobal(target.Module, i64, "__ashes_windows_iocp_port");
            LlvmApi.SetLinkage(imports.WindowsIocpPortGlobal, LlvmLinkage.Internal);
            LlvmApi.SetInitializer(imports.WindowsIocpPortGlobal, LlvmApi.ConstInt(i64, 0, 0));
        }
    }

    private static void EmitProgramModuleImportsCert(
        LlvmTargetContext target, LlvmCodegenFlavor flavor, EmitProgramModuleFlags flags, EmitProgramModuleImports imports)
    {
        if (IsWindowsFlavor(flavor) && flags.UsesNetworkingRuntimeAbi)
        {
            imports.WindowsCertOpenSystemStoreImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_CertOpenSystemStoreA");
            LlvmApi.SetLinkage(imports.WindowsCertOpenSystemStoreImport, LlvmLinkage.External);
            imports.WindowsCertEnumCertificatesInStoreImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_CertEnumCertificatesInStore");
            LlvmApi.SetLinkage(imports.WindowsCertEnumCertificatesInStoreImport, LlvmLinkage.External);
            imports.WindowsCertCloseStoreImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_CertCloseStore");
            LlvmApi.SetLinkage(imports.WindowsCertCloseStoreImport, LlvmLinkage.External);
        }
    }

    private static void EmitProgramModuleImportsMiscA(
        LlvmTargetContext target, LlvmCodegenFlavor flavor, EmitProgramModuleFlags flags,
        LlvmTypeHandle voidType, LlvmTypeHandle i32, EmitProgramModuleImports imports)
    {
        if (flags.UsesWindowsExitProcess)
        {
            LlvmTypeHandle exitProcessType = LlvmApi.FunctionType(voidType, [i32]);
            imports.WindowsExitProcessImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_ExitProcess");
            LlvmApi.SetLinkage(imports.WindowsExitProcessImport, LlvmLinkage.External);
        }

        if (IsWindowsFlavor(flavor))
        {
            imports.WindowsVirtualAllocImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_VirtualAlloc");
            LlvmApi.SetLinkage(imports.WindowsVirtualAllocImport, LlvmLinkage.External);
            imports.WindowsVirtualFreeImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_VirtualFree");
            LlvmApi.SetLinkage(imports.WindowsVirtualFreeImport, LlvmLinkage.External);
        }

        if (flags.UsesWindowsSleep)
        {
            imports.WindowsSleepImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_Sleep");
            LlvmApi.SetLinkage(imports.WindowsSleepImport, LlvmLinkage.External);
        }

        if (flags.UsesWindowsSleep || flags.UsesWindowsConsole)
        {
            LlvmValueHandle windowsGetTickCount64Import = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_GetTickCount64");
            LlvmApi.SetLinkage(windowsGetTickCount64Import, LlvmLinkage.External);
        }

        if (flags.UsesWindowsConsole)
        {
            LlvmValueHandle windowsGetConsoleModeImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_GetConsoleMode");
            LlvmApi.SetLinkage(windowsGetConsoleModeImport, LlvmLinkage.External);
            LlvmValueHandle windowsSetConsoleModeImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_SetConsoleMode");
            LlvmApi.SetLinkage(windowsSetConsoleModeImport, LlvmLinkage.External);
        }
    }

    private static void EmitProgramModuleImportsMiscB(
        LlvmTargetContext target, LlvmCodegenFlavor flavor, EmitProgramModuleFlags flags,
        LlvmTypeHandle i32, LlvmTypeHandle i8Ptr, LlvmTypeHandle i32Ptr, EmitProgramModuleImports imports)
    {
        if (flags.UsesWindowsProcess)
        {
            imports.WindowsCreatePipeImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_CreatePipe");
            LlvmApi.SetLinkage(imports.WindowsCreatePipeImport, LlvmLinkage.External);
            imports.WindowsCreateProcessAImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_CreateProcessA");
            LlvmApi.SetLinkage(imports.WindowsCreateProcessAImport, LlvmLinkage.External);
            imports.WindowsTerminateProcessImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_TerminateProcess");
            LlvmApi.SetLinkage(imports.WindowsTerminateProcessImport, LlvmLinkage.External);
            imports.WindowsGetExitCodeProcessImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_GetExitCodeProcess");
            LlvmApi.SetLinkage(imports.WindowsGetExitCodeProcessImport, LlvmLinkage.External);
        }

        if (flags.UsesWindowsProcess || flags.UsesWindowsConsole)
        {
            imports.WindowsWaitForSingleObjectImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_WaitForSingleObject");
            LlvmApi.SetLinkage(imports.WindowsWaitForSingleObjectImport, LlvmLinkage.External);
        }

        if (flags.UsesWindowsProgramArgs)
        {
            LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(target.Context);
            LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
            LlvmTypeHandle i16PtrPtr = LlvmApi.PointerTypeInContext(target.Context, 0);
            LlvmTypeHandle getCommandLineType = LlvmApi.FunctionType(i16Ptr, []);
            LlvmTypeHandle wideCharToMultiByteType = LlvmApi.FunctionType(i32, [i32, i32, i16Ptr, i32, i8Ptr, i32, i8Ptr, i8Ptr]);
            LlvmTypeHandle localFreeType = LlvmApi.FunctionType(i8Ptr, [i8Ptr]);
            LlvmTypeHandle commandLineToArgvType = LlvmApi.FunctionType(i16PtrPtr, [i16Ptr, i32Ptr]);

            imports.WindowsGetCommandLineImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_GetCommandLineW");
            LlvmApi.SetLinkage(imports.WindowsGetCommandLineImport, LlvmLinkage.External);
            imports.WindowsWideCharToMultiByteImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_WideCharToMultiByte");
            LlvmApi.SetLinkage(imports.WindowsWideCharToMultiByteImport, LlvmLinkage.External);
            imports.WindowsLocalFreeImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_LocalFree");
            LlvmApi.SetLinkage(imports.WindowsLocalFreeImport, LlvmLinkage.External);
            imports.WindowsCommandLineToArgvImport = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), "__imp_CommandLineToArgvW");
            LlvmApi.SetLinkage(imports.WindowsCommandLineToArgvImport, LlvmLinkage.External);
        }
    }

    private static LlvmValueHandle EmitProgramModuleEnsureWindowsImport(LlvmTargetContext target, string name)
    {
        LlvmValueHandle existing = LlvmApi.GetNamedGlobal(target.Module, name);
        if (existing != default)
        {
            return existing;
        }

        LlvmValueHandle g = LlvmApi.AddGlobal(target.Module, LlvmApi.PointerTypeInContext(target.Context, 0), name);
        LlvmApi.SetLinkage(g, LlvmLinkage.External);
        return g;
    }

    private static void EmitProgramModuleWorkerCap(
        LlvmTargetContext target, LlvmCodegenFlavor flavor, EmitProgramModuleFlags flags, LlvmAttributeHandle nounwindAttr)
    {
        // serve's fork-based multi-reactor (forkWorkers) resolves its worker count via the shared
        // effective-cap function, so it honors the same --parallel-workers cap and withWorkers
        // override as Ashes.Task.Parallel. The fork step function is part of the always-emitted networking
        // step table, and on Linux its body calls the cap function, so the cap globals/fn must exist
        // for every Linux networking program (not only ones that use forkWorkers). Emit them
        // (idempotent — the parallel runtime below reuses them) before the networking runtime. Windows
        // needs nothing: there the fork step is a single process and never consults the cap.
        if ((flavor == LlvmCodegenFlavor.LinuxX64 || flavor == LlvmCodegenFlavor.LinuxArm64)
            && flags.UsesNetworkingRuntimeAbi)
        {
            EmitWorkerCapInfrastructure(target, flavor, nounwindAttr);
        }
    }

    private static void EmitProgramModuleNetworking(
        LlvmTargetContext target, LlvmCodegenFlavor flavor, EmitProgramModuleFlags flags, EmitProgramModuleArena arena,
        EmitProgramModuleImports imports, LlvmTypeHandle i32, LlvmTypeHandle i32Ptr, bool usesTlsRuntime, LlvmAttributeHandle nounwindAttr)
    {
        if (flags.UsesNetworkingRuntimeAbi)
        {
            EmitNetworkingRuntimeAbi(
                target,
                flavor,
                i32,
                i32Ptr,
                arena.HeapCursorGlobal,
                arena.HeapEndGlobal,
                imports.WindowsGetStdHandleImport,
                imports.WindowsWriteFileImport,
                imports.WindowsReadFileImport,
                imports.WindowsCreateFileImport,
                imports.WindowsCloseHandleImport,
                imports.WindowsGetFileAttributesImport,
                imports.WindowsWsaStartupImport,
                imports.WindowsSocketImport,
                imports.WindowsConnectImport,
                imports.WindowsSendImport,
                imports.WindowsRecvImport,
                imports.WindowsCloseSocketImport,
                imports.WindowsIoctlSocketImport,
                imports.WindowsWsaGetLastErrorImport,
                imports.WindowsWsaPollImport,
                imports.WindowsLoadLibraryImport,
                imports.WindowsGetProcAddressImport,
                imports.WindowsCertOpenSystemStoreImport,
                imports.WindowsCertEnumCertificatesInStoreImport,
                imports.WindowsCertCloseStoreImport,
                imports.WindowsBindImport,
                imports.WindowsSetSockOptImport,
                imports.WindowsWsaIoctlImport,
                imports.WindowsWsaSendImport,
                imports.WindowsWsaRecvImport,
                imports.WindowsCreateIoCompletionPortImport,
                imports.WindowsGetQueuedCompletionStatusImport,
                imports.WindowsIocpPortGlobal,
                imports.WindowsExitProcessImport,
                imports.WindowsGetCommandLineImport,
                imports.WindowsWideCharToMultiByteImport,
                imports.WindowsLocalFreeImport,
                imports.WindowsCommandLineToArgvImport,
                imports.WindowsSleepImport,
                imports.WindowsVirtualAllocImport,
                imports.WindowsVirtualFreeImport,
                usesTlsRuntime,
                nounwindAttr);
        }
    }

    private static void EmitProgramModuleParallel(
        LlvmTargetContext target, IrProgram program, LlvmCodegenFlavor flavor, EmitProgramModuleFlags flags, LlvmAttributeHandle nounwindAttr)
    {
        bool usesParallelQueue = ProgramUsesInstruction<IrInst.ParallelQueueStart>(program);
        // withWorkers around no actual fork still needs the runtime globals (the override slot);
        // its Load/Store override instructions gate the runtime in too.
        bool usesWorkerOverride = ProgramUsesInstruction<IrInst.LoadParallelWorkerOverride>(program);
        if (ProgramUsesInstruction<IrInst.ParallelFork>(program) || usesParallelQueue || usesWorkerOverride)
        {
            if (IsWindowsFlavor(flavor))
            {
                // Worker spawn/join on win-x64 uses these kernel32 imports (looked up by name in
                // LlvmCodegenParallel). VirtualAlloc/VirtualFree are already created above for every
                // win-x64 program; CreateThread is new, and WaitForSingleObject/CloseHandle may not
                // exist yet if the program uses neither process nor file/socket IO.
                EmitProgramModuleEnsureWindowsImport(target, "__imp_CreateThread");
                EmitProgramModuleEnsureWindowsImport(target, "__imp_WaitForSingleObject");
                EmitProgramModuleEnsureWindowsImport(target, "__imp_CloseHandle");
                // Worker-cap auto-detection reads the machine's processor count.
                EmitProgramModuleEnsureWindowsImport(target, "__imp_GetSystemInfo");
                // The queued-reduce await polls with Sleep(1) (no futex on win-x64).
                EmitProgramModuleEnsureWindowsImport(target, "__imp_Sleep");
            }

            // arm64 workers need the TLS arena to get their own per-thread arena; it's now always
            // enabled on arm64 (networking coexists), so the parallel runtime is always emitted.
            if (flavor != LlvmCodegenFlavor.LinuxArm64 || flags.Arm64UsesTlsArena)
            {
                EmitParallelRuntime(target, flavor, nounwindAttr);
                if (usesParallelQueue)
                {
                    EmitParallelQueueRuntime(target, flavor, nounwindAttr);
                }
            }
        }
    }

    private static void EmitProgramModuleFunctions(
        LlvmTargetContext target, IrProgram program, string entryFunctionName, LlvmCodegenFlavor flavor,
        Backends.BackendCompileOptions options, EmitProgramModuleFlags flags, EmitProgramModuleArena arena,
        EmitProgramModuleImports imports, LlvmTypeHandle i32, LlvmTypeHandle i32Ptr, LlvmTypeHandle i64,
        LlvmTypeHandle voidType, LlvmTypeHandle closureFunctionType,
        IReadOnlyDictionary<string, string> stringLiterals, LlvmAttributeHandle nounwindAttr)
    {
        LlvmValueHandle entryFunction = LlvmApi.AddFunction(target.Module,
            entryFunctionName,
            IsLinuxFlavor(flavor)
                ? LlvmApi.FunctionType(voidType, [i64])
                : LlvmApi.FunctionType(voidType, []));
        LlvmApi.SetLinkage(entryFunction, LlvmLinkage.External);
        LlvmApi.AddAttributeAtIndex(entryFunction, LlvmApi.AttributeIndexFunction, nounwindAttr);

        var liftedFunctions = new Dictionary<string, LlvmValueHandle>(StringComparer.Ordinal);
        foreach (IrFunction function in program.Functions)
        {
            LlvmValueHandle llvmFunction = LlvmApi.AddFunction(target.Module, function.Label, closureFunctionType);
            LlvmApi.SetLinkage(llvmFunction, LlvmLinkage.Internal);
            LlvmApi.AddAttributeAtIndex(llvmFunction, LlvmApi.AttributeIndexFunction, nounwindAttr);
            liftedFunctions.Add(function.Label, llvmFunction);
        }

        // Debug info setup
        using var dbg = CreateDebugInfoContext(target, options, program);
        if (dbg is not null)
        {
            SetupFunctionDebugInfo(dbg, entryFunction, program.EntryFunction);
            foreach (IrFunction function in program.Functions)
            {
                SetupFunctionDebugInfo(dbg, liftedFunctions[function.Label], function);
            }
        }

        EmitProgramModuleEmitEntry(target, program, entryFunction, flavor, flags, arena, imports, i32, i32Ptr, stringLiterals, liftedFunctions, dbg);
        EmitProgramModuleEmitLifted(target, program, flavor, flags, arena, imports, i32, i32Ptr, stringLiterals, liftedFunctions, dbg);
        dbg?.FinalizeDebugInfo();
    }

    private static void EmitProgramModuleEmitEntry(
        LlvmTargetContext target, IrProgram program, LlvmValueHandle entryFunction, LlvmCodegenFlavor flavor,
        EmitProgramModuleFlags flags, EmitProgramModuleArena arena, EmitProgramModuleImports imports,
        LlvmTypeHandle i32, LlvmTypeHandle i32Ptr, IReadOnlyDictionary<string, string> stringLiterals,
        IReadOnlyDictionary<string, LlvmValueHandle> liftedFunctions, DebugInfoContext? dbg)
    {
        EmitFunctionBody(
            target, entryFunction, program.EntryFunction,
            stringLiterals, liftedFunctions, flavor,
            flags.UsesProgramArgs, flags.UseRunQueueScheduler, i32,
            i32Ptr, arena.HeapCursorGlobal, arena.HeapEndGlobal,
            arena.ToSpaceCursorGlobal, arena.ToSpaceEndGlobal, imports.WindowsGetStdHandleImport,
            imports.WindowsWriteFileImport, imports.WindowsReadFileImport, imports.WindowsCreateFileImport,
            imports.WindowsCloseHandleImport, imports.WindowsGetFileAttributesImport, imports.WindowsWsaStartupImport,
            imports.WindowsSocketImport, imports.WindowsConnectImport, imports.WindowsSendImport,
            imports.WindowsRecvImport, imports.WindowsCloseSocketImport, imports.WindowsIoctlSocketImport,
            imports.WindowsWsaGetLastErrorImport, imports.WindowsWsaPollImport, imports.WindowsLoadLibraryImport,
            imports.WindowsGetProcAddressImport, imports.WindowsCertOpenSystemStoreImport, imports.WindowsCertEnumCertificatesInStoreImport,
            imports.WindowsCertCloseStoreImport, imports.WindowsBindImport, imports.WindowsSetSockOptImport,
            imports.WindowsWsaIoctlImport, imports.WindowsWsaSendImport, imports.WindowsWsaRecvImport,
            imports.WindowsCreateIoCompletionPortImport, imports.WindowsGetQueuedCompletionStatusImport, imports.WindowsIocpPortGlobal,
            imports.WindowsExitProcessImport, imports.WindowsGetCommandLineImport, imports.WindowsWideCharToMultiByteImport,
            imports.WindowsLocalFreeImport, imports.WindowsCommandLineToArgvImport, imports.WindowsSleepImport,
            imports.WindowsVirtualAllocImport, imports.WindowsVirtualFreeImport, imports.WindowsCreatePipeImport,
            imports.WindowsCreateProcessAImport, imports.WindowsTerminateProcessImport, imports.WindowsWaitForSingleObjectImport,
            imports.WindowsGetExitCodeProcessImport,
            isEntry: true, arm64UsesTlsArena: flags.Arm64UsesTlsArena, debugContext: dbg);
    }

    private static void EmitProgramModuleEmitLifted(
        LlvmTargetContext target, IrProgram program, LlvmCodegenFlavor flavor, EmitProgramModuleFlags flags,
        EmitProgramModuleArena arena, EmitProgramModuleImports imports, LlvmTypeHandle i32, LlvmTypeHandle i32Ptr,
        IReadOnlyDictionary<string, string> stringLiterals, IReadOnlyDictionary<string, LlvmValueHandle> liftedFunctions, DebugInfoContext? dbg)
    {
        foreach (IrFunction function in program.Functions)
        {
            EmitFunctionBody(
                target, liftedFunctions[function.Label], function,
                stringLiterals, liftedFunctions, flavor,
                flags.UsesProgramArgs, flags.UseRunQueueScheduler, i32,
                i32Ptr, arena.HeapCursorGlobal, arena.HeapEndGlobal,
                arena.ToSpaceCursorGlobal, arena.ToSpaceEndGlobal, imports.WindowsGetStdHandleImport,
                imports.WindowsWriteFileImport, imports.WindowsReadFileImport, imports.WindowsCreateFileImport,
                imports.WindowsCloseHandleImport, imports.WindowsGetFileAttributesImport, imports.WindowsWsaStartupImport,
                imports.WindowsSocketImport, imports.WindowsConnectImport, imports.WindowsSendImport,
                imports.WindowsRecvImport, imports.WindowsCloseSocketImport, imports.WindowsIoctlSocketImport,
                imports.WindowsWsaGetLastErrorImport, imports.WindowsWsaPollImport, imports.WindowsLoadLibraryImport,
                imports.WindowsGetProcAddressImport, imports.WindowsCertOpenSystemStoreImport, imports.WindowsCertEnumCertificatesInStoreImport,
                imports.WindowsCertCloseStoreImport, imports.WindowsBindImport, imports.WindowsSetSockOptImport,
                imports.WindowsWsaIoctlImport, imports.WindowsWsaSendImport, imports.WindowsWsaRecvImport,
                imports.WindowsCreateIoCompletionPortImport, imports.WindowsGetQueuedCompletionStatusImport, imports.WindowsIocpPortGlobal,
                imports.WindowsExitProcessImport, imports.WindowsGetCommandLineImport, imports.WindowsWideCharToMultiByteImport,
                imports.WindowsLocalFreeImport, imports.WindowsCommandLineToArgvImport, imports.WindowsSleepImport,
                imports.WindowsVirtualAllocImport, imports.WindowsVirtualFreeImport, imports.WindowsCreatePipeImport,
                imports.WindowsCreateProcessAImport, imports.WindowsTerminateProcessImport, imports.WindowsWaitForSingleObjectImport,
                imports.WindowsGetExitCodeProcessImport,
                isEntry: false, debugContext: dbg);
        }
    }

    private static bool ProgramUsesInstruction<TInstruction>(IrProgram program)
        where TInstruction : IrInst
    {
        return program.EntryFunction.Instructions.Any(static instruction => instruction is TInstruction)
            || program.Functions.Any(static function => function.Instructions.Any(static instruction => instruction is TInstruction));
    }

    private static bool ProgramUsesTlsRuntimeAbi(IrProgram program)
    {
        return ProgramUsesInstruction<IrInst.HttpGet>(program)
            || ProgramUsesInstruction<IrInst.HttpPost>(program)
            || ProgramUsesInstruction<IrInst.CreateHttpGetTask>(program)
            || ProgramUsesInstruction<IrInst.CreateHttpPostTask>(program)
            || ProgramUsesInstruction<IrInst.CreateTlsConnectTask>(program)
            || ProgramUsesInstruction<IrInst.CreateTlsHandshakeTask>(program)
            || ProgramUsesInstruction<IrInst.CreateTlsServerHandshakeTask>(program)
            || ProgramUsesInstruction<IrInst.CreateTlsSendTask>(program)
            || ProgramUsesInstruction<IrInst.CreateTlsReceiveTask>(program)
            || ProgramUsesInstruction<IrInst.CreateTlsCloseTask>(program);
    }

    private static bool ProgramUsesMathRuntimeAbi(IrProgram program)
    {
        return ProgramUsesInstruction<IrInst.CallLibm>(program);
    }

    private static bool ProgramUsesRegexRuntimeAbi(IrProgram program)
    {
        return ProgramUsesInstruction<IrInst.RegexCompile>(program)
            || ProgramUsesInstruction<IrInst.RegexCompileError>(program)
            || ProgramUsesInstruction<IrInst.RegexFind>(program)
            || ProgramUsesInstruction<IrInst.RegexCaptures>(program)
            || ProgramUsesInstruction<IrInst.RegexSubstitute>(program);
    }

    private static bool ProgramUsesBigIntRuntimeAbi(IrProgram program)
    {
        return ProgramUsesInstruction<IrInst.BigIntFromInt>(program)
            || ProgramUsesInstruction<IrInst.BigIntToString>(program)
            || ProgramUsesInstruction<IrInst.BigIntBinary>(program)
            || ProgramUsesInstruction<IrInst.BigIntCompare>(program);
    }

    /// <summary>
    /// When the program calls a Layer-2 transcendental, parses the vendored openlibm bitcode and
    /// links it into the program module so the referenced symbols (<c>sin</c>, <c>pow</c>, …)
    /// resolve internally — no dynamic import, no runtime dependency. Hermetic-only programs link
    /// nothing.
    /// </summary>
    private static void LinkOpenlibmBitcodeIfNeeded(LlvmTargetContext target, IrProgram program, string targetId)
    {
        if (!ProgramUsesMathRuntimeAbi(program))
        {
            return;
        }

        byte[] bitcode = HermeticRuntimeAssets.Openlibm.GetBitcode(targetId);
        if (!LlvmApi.TryParseModule(target.Context, bitcode, "openlibm", out var openlibmModule, out string? error))
        {
            throw new InvalidOperationException($"Failed to parse openlibm bitcode for '{targetId}': {error}");
        }

        NormalizeBitcodeTriple(openlibmModule, target);

        // LLVMLinkModules2 consumes (and disposes) the source module. Returns non-zero on failure.
        // The vendored bitcode is the whole (small, ~190 KB) self-contained openlibm, kept intact:
        // it must retain the standard libm functions because the backend's own instruction selection
        // lowers some intrinsics (e.g. llvm.round) to libm libcalls (round, floor, …) resolved
        // against these definitions. Dead-stripping to only the referenced functions is a documented
        // future size optimization; it requires preserving those libcall targets.
        if (LlvmApi.LinkModules2(target.Module, openlibmModule) != 0)
        {
            throw new InvalidOperationException($"Failed to link openlibm bitcode into the program module for '{targetId}'.");
        }
    }

    /// <summary>
    /// When the program uses Ashes.Text.Regex, parses the vendored PCRE2 8-bit bitcode and links it into
    /// the program module so the pcre2_* symbols resolve internally — no dynamic import, no runtime
    /// dependency. The payload's only external symbols are malloc/free (routed to the PCRE2 region
    /// emitted by <see cref="EmitPcre2Allocator"/>) and memcpy/memset (the module's own builtins).
    /// Linked after the program's optimization passes, mirroring the openlibm path.
    /// </summary>
    private static void LinkPcre2BitcodeIfNeeded(LlvmTargetContext target, IrProgram program, string targetId)
    {
        if (!ProgramUsesRegexRuntimeAbi(program))
        {
            return;
        }

        byte[] bitcode = HermeticRuntimeAssets.Pcre2.GetBitcode(targetId);
        if (!LlvmApi.TryParseModule(target.Context, bitcode, "pcre2", out var pcre2Module, out string? error))
        {
            throw new InvalidOperationException($"Failed to parse PCRE2 bitcode for '{targetId}': {error}");
        }

        NormalizeBitcodeTriple(pcre2Module, target);

        // LLVMLinkModules2 consumes (and disposes) the source module. Returns non-zero on failure.
        if (LlvmApi.LinkModules2(target.Module, pcre2Module) != 0)
        {
            throw new InvalidOperationException($"Failed to link PCRE2 bitcode into the program module for '{targetId}'.");
        }
    }

    private static void LinkMbedTlsBitcodeIfNeeded(LlvmTargetContext target, IrProgram program, string targetId)
    {
        if (!ProgramUsesTlsRuntimeAbi(program))
        {
            return;
        }

        byte[] bitcode = HermeticRuntimeAssets.MbedTls.GetBitcode(targetId);
        if (!LlvmApi.TryParseModule(target.Context, bitcode, "mbedtls", out var mbedTlsModule, out string? error))
        {
            throw new InvalidOperationException($"Failed to parse Mbed TLS bitcode for '{targetId}': {error}");
        }

        NormalizeBitcodeTriple(mbedTlsModule, target);

        if (LlvmApi.LinkModules2(target.Module, mbedTlsModule) != 0)
        {
            throw new InvalidOperationException($"Failed to link Mbed TLS bitcode into the program module for '{targetId}'.");
        }
    }

    /// <summary>
    /// Rewrites a vendored bitcode payload's target triple to the program module's triple before
    /// linking. The Windows payloads are compiled with the <c>windows-gnu</c> triple (the sources
    /// parse under MinGW stub headers with no MSVC SDK) while the backend emits a <c>windows-msvc</c>
    /// module. The two share an identical datalayout — which is what <c>LLVMLinkModules2</c> actually
    /// gates on — so linking is correct either way, but a triple-string mismatch makes the linker emit
    /// a "Linking two modules of different target triples" warning. Normalizing the source triple to
    /// the destination's silences that noise without touching the payloads. A no-op on Linux, where
    /// the payload and program triples already match.
    /// </summary>
    private static void NormalizeBitcodeTriple(LlvmModuleHandle bitcodeModule, LlvmTargetContext target)
    {
        LlvmApi.SetTarget(bitcodeModule, target.TargetTriple);
    }

    private static string GetTargetIdForFlavor(LlvmCodegenFlavor flavor)
    {
        return flavor switch
        {
            LlvmCodegenFlavor.LinuxX64 => Backends.TargetIds.LinuxX64,
            LlvmCodegenFlavor.LinuxArm64 => Backends.TargetIds.LinuxArm64,
            LlvmCodegenFlavor.WindowsX64 => Backends.TargetIds.WindowsX64,
            LlvmCodegenFlavor.WindowsArm64 => Backends.TargetIds.WindowsArm64,
            _ => throw new ArgumentOutOfRangeException(nameof(flavor), $"Unsupported codegen flavor '{flavor}'.")
        };
    }

    private static bool RequiresEntryHeapStorage(IrInst instruction)
    {
        return instruction is IrInst.Alloc or IrInst.AllocAdt or IrInst.AllocAdtToSpace or IrInst.ConcatStr or IrInst.MakeClosure or IrInst.LoadProgramArgs or IrInst.CopyOutArena or IrInst.CopyOutArenaToSpace or IrInst.CopyOutList or IrInst.CopyOutClosure or IrInst.CopyOutTcoListCell;
    }

    private static void EmitFunctionBody(
        LlvmTargetContext target,
        LlvmValueHandle llvmFunction,
        IrFunction function,
        IReadOnlyDictionary<string, string> stringLiterals,
        IReadOnlyDictionary<string, LlvmValueHandle> liftedFunctions,
        LlvmCodegenFlavor flavor,
        bool usesProgramArgs,
        bool useRunQueueScheduler,
        LlvmTypeHandle i32,
        LlvmTypeHandle i32Ptr,
        LlvmValueHandle heapCursorGlobal,
        LlvmValueHandle heapEndGlobal,
        LlvmValueHandle toSpaceCursorGlobal,
        LlvmValueHandle toSpaceEndGlobal,
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
        LlvmValueHandle windowsIoctlSocketImport,
        LlvmValueHandle windowsWsaGetLastErrorImport,
        LlvmValueHandle windowsWsaPollImport,
        LlvmValueHandle windowsLoadLibraryImport,
        LlvmValueHandle windowsGetProcAddressImport,
        LlvmValueHandle windowsCertOpenSystemStoreImport,
        LlvmValueHandle windowsCertEnumCertificatesInStoreImport,
        LlvmValueHandle windowsCertCloseStoreImport,
        LlvmValueHandle windowsBindImport,
        LlvmValueHandle windowsSetSockOptImport,
        LlvmValueHandle windowsWsaIoctlImport,
        LlvmValueHandle windowsWsaSendImport,
        LlvmValueHandle windowsWsaRecvImport,
        LlvmValueHandle windowsCreateIoCompletionPortImport,
        LlvmValueHandle windowsGetQueuedCompletionStatusImport,
        LlvmValueHandle windowsIocpPortGlobal,
        LlvmValueHandle windowsExitProcessImport,
        LlvmValueHandle windowsGetCommandLineImport,
        LlvmValueHandle windowsWideCharToMultiByteImport,
        LlvmValueHandle windowsLocalFreeImport,
        LlvmValueHandle windowsCommandLineToArgvImport,
        LlvmValueHandle windowsSleepImport,
        LlvmValueHandle windowsVirtualAllocImport,
        LlvmValueHandle windowsVirtualFreeImport,
        LlvmValueHandle windowsCreatePipeImport,
        LlvmValueHandle windowsCreateProcessAImport,
        LlvmValueHandle windowsTerminateProcessImport,
        LlvmValueHandle windowsWaitForSingleObjectImport,
        LlvmValueHandle windowsGetExitCodeProcessImport,
        bool isEntry,
        bool arm64UsesTlsArena = false,
        DebugInfoContext? debugContext = null)
    {
        EmitFunctionBodySlots slots = EmitFunctionBodyAllocateSlots(target, llvmFunction, function, flavor, isEntry, debugContext);

        var state = new LlvmCodegenState(
            target, llvmFunction, stringLiterals, liftedFunctions, slots.ProgramArgsSlot,
            slots.TempSlots, slots.LocalSlots, heapCursorGlobal, heapEndGlobal, slots.LabelBlocks,
            slots.FallthroughBlocks, slots.I64, i32, slots.I8, slots.F64, LlvmApi.FloatTypeInContext(target.Context), slots.I8Ptr, i32Ptr, slots.I64Ptr,
            slots.EntryStackPointer, windowsGetStdHandleImport, windowsWriteFileImport, windowsReadFileImport,
            windowsCreateFileImport, windowsCloseHandleImport, windowsGetFileAttributesImport, windowsWsaStartupImport,
            windowsSocketImport, windowsConnectImport, windowsSendImport, windowsRecvImport, windowsCloseSocketImport,
            windowsIoctlSocketImport, windowsWsaGetLastErrorImport, windowsWsaPollImport, windowsLoadLibraryImport,
            windowsGetProcAddressImport, windowsCertOpenSystemStoreImport, windowsCertEnumCertificatesInStoreImport,
            windowsCertCloseStoreImport, windowsBindImport, windowsSetSockOptImport, windowsWsaIoctlImport,
            windowsWsaSendImport, windowsWsaRecvImport, windowsCreateIoCompletionPortImport,
            windowsGetQueuedCompletionStatusImport, windowsIocpPortGlobal, windowsExitProcessImport,
            windowsGetCommandLineImport, windowsWideCharToMultiByteImport, windowsLocalFreeImport,
            windowsCommandLineToArgvImport, windowsSleepImport, windowsVirtualAllocImport, windowsVirtualFreeImport,
            windowsCreatePipeImport, windowsCreateProcessAImport, windowsTerminateProcessImport,
            windowsWaitForSingleObjectImport, windowsGetExitCodeProcessImport,
            new Dictionary<string, LlvmValueHandle>(StringComparer.Ordinal), flavor, usesProgramArgs, isEntry) with
        {
            ToSpaceCursorSlot = toSpaceCursorGlobal,
            ToSpaceEndSlot = toSpaceEndGlobal,
            // Non-linux: the blob-region globals were created in module setup; look them up by name to
            // avoid threading them through this (very large) parameter list. On linux they are repointed
            // at the per-thread TCB just below, so the (null) lookup result here is overwritten.
            BlobCursorSlot = LlvmApi.GetNamedGlobal(target.Module, "__ashes_blob_cursor"),
            BlobEndSlot = LlvmApi.GetNamedGlobal(target.Module, "__ashes_blob_end"),
            UseRunQueueScheduler = useRunQueueScheduler,
        };

        state = EmitFunctionBodyLinuxArenaSetup(state, flavor, isEntry);

        if (isEntry && arm64UsesTlsArena)
        {
            // Must run before the first arena access (EmitHeapChunkInit below) so TPIDR_EL0 addresses
            // the thread-local arena. Self-initialises it only when no loader did (static image); a
            // dynamic image keeps the loader's thread pointer (its DTV backs libc's dynamic TLS).
            EmitArm64MainThreadTlsSetup(state);
        }

        if (isEntry)
        {
            EmitHeapChunkInit(state);
        }

        if (isEntry && usesProgramArgs)
        {
            EmitEntryProgramArgsInitialization(state);
        }

        EmitFunctionBodyInstructionLoop(state, function, debugContext, slots.I64);
    }

    private readonly record struct EmitFunctionBodySlots(
        LlvmValueHandle EntryStackPointer,
        LlvmValueHandle[] TempSlots,
        LlvmValueHandle[] LocalSlots,
        LlvmValueHandle ProgramArgsSlot,
        Dictionary<string, LlvmBasicBlockHandle> LabelBlocks,
        Dictionary<int, LlvmBasicBlockHandle> FallthroughBlocks,
        LlvmTypeHandle I64,
        LlvmTypeHandle I8,
        LlvmTypeHandle F64,
        LlvmTypeHandle I8Ptr,
        LlvmTypeHandle I64Ptr);

    private static EmitFunctionBodySlots EmitFunctionBodyAllocateSlots(
        LlvmTargetContext target,
        LlvmValueHandle llvmFunction,
        IrFunction function,
        LlvmCodegenFlavor flavor,
        bool isEntry,
        DebugInfoContext? debugContext)
    {
        LlvmTypeHandle i64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmTypeHandle i8 = LlvmApi.Int8TypeInContext(target.Context);
        LlvmTypeHandle f64 = LlvmApi.DoubleTypeInContext(target.Context);
        LlvmTypeHandle i8Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);
        LlvmTypeHandle i64Ptr = LlvmApi.PointerTypeInContext(target.Context, 0);

        LlvmBasicBlockHandle entryBlock = LlvmApi.AppendBasicBlockInContext(target.Context, llvmFunction, "entry");
        LlvmApi.PositionBuilderAtEnd(target.Builder, entryBlock);
        if (debugContext is not null)
        {
            debugContext.ClearDebugLocation(target.Builder);
        }

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

        // Emit DWARF debug variable declarations for named locals (after allocas, in entry block)
        if (debugContext is not null)
        {
            EmitLocalVariableDebugInfo(debugContext, target.Builder, function, localSlots);
        }

        var labelBlocks = new Dictionary<string, LlvmBasicBlockHandle>(StringComparer.Ordinal);
        foreach (IrInst.Label label in function.Instructions.OfType<IrInst.Label>())
        {
            labelBlocks[label.Name] = LlvmApi.AppendBasicBlockInContext(target.Context, llvmFunction, label.Name);
        }

        var fallthroughBlocks = new Dictionary<int, LlvmBasicBlockHandle>();
        return new EmitFunctionBodySlots(
            entryStackPointer, tempSlots, localSlots, programArgsSlot,
            labelBlocks, fallthroughBlocks, i64, i8, f64, i8Ptr, i64Ptr);
    }

    private static LlvmCodegenState EmitFunctionBodyLinuxArenaSetup(
        LlvmCodegenState state, LlvmCodegenFlavor flavor, bool isEntry)
    {
        if (flavor != LlvmCodegenFlavor.LinuxX64 && !IsWindowsFlavor(flavor))
        {
            return state;
        }

        // Recover this thread's TCB base and address the arena cursor/end (and to-space/blob)
        // through it as ordinary pointers, so worker threads get their own arenas. On linux the
        // entry sets up GS (arch_prctl) + the TCB self-pointer and others read %gs:0; on Windows
        // the TCB pointer lives in TEB+0x28 (win-x64 reaches the TEB via GS, win-arm64 via x18;
        // the OS provides the TEB, so no arch_prctl).
        LlvmValueHandle tcbBase;
        if (flavor == LlvmCodegenFlavor.LinuxX64)
        {
            tcbBase = isEntry ? EmitMainThreadTlsInit(state) : EmitReadTcbBaseFromGs(state);
        }
        else
        {
            tcbBase = isEntry ? EmitMainThreadTlsInitWindows(state) : EmitReadTcbBaseFromTeb(state);
        }

        (LlvmValueHandle cursorSlot, LlvmValueHandle endSlot) = BuildLinuxArenaSlots(state, tcbBase);
        (LlvmValueHandle toCursorSlot, LlvmValueHandle toEndSlot) = BuildLinuxTcbSlots(state, tcbBase, TcbToSpaceCursorOffset, TcbToSpaceEndOffset);
        (LlvmValueHandle blobCursorSlot, LlvmValueHandle blobEndSlot) = BuildLinuxTcbSlots(state, tcbBase, TcbBlobCursorOffset, TcbBlobEndOffset);
        return state with
        {
            HeapCursorSlot = cursorSlot,
            HeapEndSlot = endSlot,
            ToSpaceCursorSlot = toCursorSlot,
            ToSpaceEndSlot = toEndSlot,
            BlobCursorSlot = blobCursorSlot,
            BlobEndSlot = blobEndSlot,
        };
    }

    private static void EmitFunctionBodyInstructionLoop(
        LlvmCodegenState state, IrFunction function, DebugInfoContext? debugContext, LlvmTypeHandle i64)
    {
        LlvmTargetContext target = state.Target;
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

                // Label blocks are pre-created (forward jumps need the handles), which appends
                // them ahead of any blocks the instruction expansions create later. Move each
                // into flow position as it is reached: physical block order then follows IR
                // order, so the lowest address of a source line is the line's first executed
                // instruction (a breakpoint on an `if` line must bind to the condition, not to
                // a join/else block that happened to be laid out earlier).
                LlvmApi.MoveBasicBlockAfter(state.GetLabelBlock(label.Name), LlvmApi.GetInsertBlock(target.Builder));
                LlvmApi.PositionBuilderAtEnd(target.Builder, state.GetLabelBlock(label.Name));
                terminated = false;
                continue;
            }

            if (terminated)
            {
                var fallthrough = state.GetOrCreateFallthroughBlock(index);
                LlvmApi.MoveBasicBlockAfter(fallthrough, LlvmApi.GetInsertBlock(target.Builder));
                LlvmApi.PositionBuilderAtEnd(target.Builder, fallthrough);
                terminated = false;
            }

            EmitInstructionDebugLocation(debugContext, target.Builder, instruction, function.Label);
            terminated = EmitInstruction(state, instruction, index, function.Instructions);
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

    private static bool EmitInstruction(LlvmCodegenState state, IrInst instruction, int index, IReadOnlyList<IrInst> instructions)
    {
        return EmitInstructionGroup1(state, instruction)
            ?? EmitInstructionGroup2(state, instruction)
            ?? EmitInstructionGroup3(state, instruction)
            ?? EmitInstructionGroup4(state, instruction)
            ?? EmitInstructionGroup5(state, instruction)
            ?? EmitInstructionGroup6(state, instruction, index, instructions)
            ?? EmitInstructionGroup7(state, instruction, index)
            ?? throw new InvalidOperationException($"The LLVM Linux backend does not yet support instruction '{instruction.GetType().Name}'.");
    }

    private static bool? EmitInstructionGroup1(LlvmCodegenState state, IrInst instruction)
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
            IrInst.ConsoleEnableRaw consoleEnableRaw => StoreTemp(state, consoleEnableRaw.Target, EmitConsoleEnableRaw(state)),
            IrInst.ConsoleRestore consoleRestore => StoreTemp(state, consoleRestore.Target, EmitConsoleRestore(state)),
            IrInst.ConsolePoll consolePoll => StoreTemp(state, consolePoll.Target, EmitConsolePoll(state, LoadTemp(state, consolePoll.TimeoutTemp))),
            IrInst.MonotonicMillis monotonicMillis => StoreTemp(state, monotonicMillis.Target, EmitMonotonicNowMs(state, "monotonic_millis")),
            IrInst.FileReadText fileReadText => StoreTemp(state, fileReadText.Target, EmitFileReadText(state, LoadTemp(state, fileReadText.PathTemp))),
            IrInst.FileReadAllBytes fileReadAllBytes => StoreTemp(state, fileReadAllBytes.Target, EmitFileReadText(state, LoadTemp(state, fileReadAllBytes.PathTemp), rawBytes: true)),
            IrInst.FileMmap fileMmap => StoreTemp(state, fileMmap.Target, EmitFileMmap(state, LoadTemp(state, fileMmap.PathTemp))),
            IrInst.FileOpen fileOpen => StoreTemp(state, fileOpen.Target, EmitFileOpen(state, LoadTemp(state, fileOpen.PathTemp))),
            IrInst.FileReadChunk fileReadChunk => StoreTemp(state, fileReadChunk.Target, EmitFileReadChunk(state, LoadTemp(state, fileReadChunk.HandleTemp), LoadTemp(state, fileReadChunk.CountTemp))),
            IrInst.FileReadLine fileReadLine => StoreTemp(state, fileReadLine.Target, EmitFileReadLine(state, LoadTemp(state, fileReadLine.HandleTemp))),
            IrInst.FileClose fileClose => StoreTemp(state, fileClose.Target, EmitFileClose(state, LoadTemp(state, fileClose.HandleTemp))),
            IrInst.FileWriteText fileWriteText => StoreTemp(state, fileWriteText.Target, EmitFileWriteText(state, LoadTemp(state, fileWriteText.PathTemp), LoadTemp(state, fileWriteText.TextTemp))),
            IrInst.FileExists fileExists => StoreTemp(state, fileExists.Target, EmitFileExists(state, LoadTemp(state, fileExists.PathTemp))),
            IrInst.TextUncons textUncons => StoreTemp(state, textUncons.Target, EmitTextUncons(state, LoadTemp(state, textUncons.TextTemp))),
            IrInst.TextParseInt textParseInt => StoreTemp(state, textParseInt.Target, EmitTextParseInt(state, LoadTemp(state, textParseInt.TextTemp))),
            IrInst.TextParseFloat textParseFloat => StoreTemp(state, textParseFloat.Target, EmitTextParseFloat(state, LoadTemp(state, textParseFloat.TextTemp))),
            IrInst.TextFromInt textFromInt => StoreTemp(state, textFromInt.Target, EmitSignedIntToString(state, LoadTemp(state, textFromInt.ValueTemp), "text_from_int")),
            IrInst.TextFromFloat textFromFloat => StoreTemp(state, textFromFloat.Target, EmitFloatToString(state, LoadTempAsFloat(state, textFromFloat.ValueTemp), "text_from_float")),
            IrInst.TextFormatFloat textFormatFloat => StoreTemp(state, textFormatFloat.Target, EmitFloatToFixedString(state, LoadTempAsFloat(state, textFormatFloat.ValueTemp), LoadTemp(state, textFormatFloat.DecimalsTemp), "text_format_float")),
            IrInst.TextToHex textToHex => StoreTemp(state, textToHex.Target, EmitIntToHexString(state, LoadTemp(state, textToHex.ValueTemp), "text_to_hex")),
            IrInst.TextAsciiCase asciiCase => StoreTemp(state, asciiCase.Target, EmitAsciiCaseString(state, LoadTemp(state, asciiCase.SourceTemp), asciiCase.Upper, "text_ascii_case")),
            IrInst.BigIntFromInt bigIntFromInt => StoreTemp(state, bigIntFromInt.Target, EmitBigIntFromInt(state, LoadTemp(state, bigIntFromInt.ValueTemp))),
            IrInst.BigIntToString bigIntToString => StoreTemp(state, bigIntToString.Target, EmitBigIntToString(state, LoadTemp(state, bigIntToString.ValueTemp))),
            IrInst.BigIntToInt bigIntToInt => StoreTemp(state, bigIntToInt.Target, EmitBigIntToInt(state, LoadTemp(state, bigIntToInt.ValueTemp))),
            IrInst.BigIntFromString bigIntFromString => StoreTemp(state, bigIntFromString.Target, EmitBigIntFromString(state, LoadTemp(state, bigIntFromString.ValueTemp))),
            IrInst.BigIntBinary bigIntBinary => StoreTemp(state, bigIntBinary.Target, EmitBigIntBinary(state, LoadTemp(state, bigIntBinary.Left), LoadTemp(state, bigIntBinary.Right), bigIntBinary.Op)),
            IrInst.BigIntCompare bigIntCompare => StoreTemp(state, bigIntCompare.Target, EmitBigIntCompare(state, LoadTemp(state, bigIntCompare.Left), LoadTemp(state, bigIntCompare.Right))),
            _ => (bool?)null,
        };
    }

    private static bool? EmitInstructionGroup2(LlvmCodegenState state, IrInst instruction)
    {
        return instruction switch
        {
            IrInst.HttpGet httpGet => StoreTemp(state, httpGet.Target, EmitHttpGetAbiCall(state, LoadTemp(state, httpGet.UrlTemp))),
            IrInst.HttpPost httpPost => StoreTemp(state, httpPost.Target, EmitHttpPostAbiCall(state, LoadTemp(state, httpPost.UrlTemp), LoadTemp(state, httpPost.BodyTemp))),
            IrInst.NetTcpConnect tcpConnect => StoreTemp(state, tcpConnect.Target, EmitTcpConnectAbiCall(state, LoadTemp(state, tcpConnect.HostTemp), LoadTemp(state, tcpConnect.PortTemp))),
            IrInst.NetTcpSend tcpSend => StoreTemp(state, tcpSend.Target, EmitTcpSendAbiCall(state, LoadTemp(state, tcpSend.SocketTemp), LoadTemp(state, tcpSend.TextTemp))),
            IrInst.NetTcpReceive tcpReceive => StoreTemp(state, tcpReceive.Target, EmitTcpReceiveAbiCall(state, LoadTemp(state, tcpReceive.SocketTemp), LoadTemp(state, tcpReceive.MaxBytesTemp))),
            IrInst.NetTcpClose tcpClose => StoreTemp(state, tcpClose.Target, EmitTcpCloseAbiCall(state, LoadTemp(state, tcpClose.SocketTemp))),
            IrInst.NetTcpListen tcpListen => StoreTemp(state, tcpListen.Target, EmitTcpListenAbiCall(state, LoadTemp(state, tcpListen.PortTemp))),
            IrInst.NetTcpAccept tcpAccept => StoreTemp(state, tcpAccept.Target, EmitTcpAcceptAbiCall(state, LoadTemp(state, tcpAccept.SocketTemp))),
            IrInst.BytesEmpty bytesEmpty => StoreTemp(state, bytesEmpty.Target, EmitBytesEmpty(state)),
            IrInst.BytesSingleton bytesSingleton => StoreTemp(state, bytesSingleton.Target, EmitBytesSingleton(state, LoadTemp(state, bytesSingleton.ByteTemp))),
            IrInst.BytesLength bytesLength => StoreTemp(state, bytesLength.Target, EmitBytesLength(state, LoadTemp(state, bytesLength.BytesTemp))),
            IrInst.BytesGet bytesGet => StoreTemp(state, bytesGet.Target, EmitBytesGet(state, LoadTemp(state, bytesGet.BytesTemp), LoadTemp(state, bytesGet.IndexTemp))),
            IrInst.BytesIndexOf bytesIndexOf => StoreTemp(state, bytesIndexOf.Target, EmitBytesIndexOf(state, LoadTemp(state, bytesIndexOf.BytesTemp), LoadTemp(state, bytesIndexOf.NeedleTemp), LoadTemp(state, bytesIndexOf.FromTemp))),
            IrInst.BytesCompare bytesCompare => StoreTemp(state, bytesCompare.Target, EmitBytesCompare(state, LoadTemp(state, bytesCompare.LeftTemp), LoadTemp(state, bytesCompare.RightTemp))),
            IrInst.BytesScanHash bytesScanHash => StoreTemp(state, bytesScanHash.Target, EmitBytesScanHash(state, LoadTemp(state, bytesScanHash.BytesTemp), LoadTemp(state, bytesScanHash.NeedleTemp), LoadTemp(state, bytesScanHash.FromTemp))),
            IrInst.BytesSubText bytesSubText => StoreTemp(state, bytesSubText.Target, EmitBytesSubText(state, LoadTemp(state, bytesSubText.BytesTemp), LoadTemp(state, bytesSubText.StartTemp), LoadTemp(state, bytesSubText.LenTemp))),
            IrInst.BytesSubView bytesSubView => StoreTemp(state, bytesSubView.Target, EmitBytesSubView(state, LoadTemp(state, bytesSubView.BytesTemp), LoadTemp(state, bytesSubView.StartTemp), LoadTemp(state, bytesSubView.LenTemp))),
            IrInst.BytesAppend bytesAppend => StoreTemp(state, bytesAppend.Target, EmitBytesAppend(state, LoadTemp(state, bytesAppend.LeftTemp), LoadTemp(state, bytesAppend.RightTemp))),
            IrInst.BytesAppendByte bytesAppendByte => StoreTemp(state, bytesAppendByte.Target, EmitBytesAppendByte(state, LoadTemp(state, bytesAppendByte.BytesTemp), LoadTemp(state, bytesAppendByte.ByteTemp))),
            IrInst.BytesFromList bytesFromList => StoreTemp(state, bytesFromList.Target, EmitBytesFromList(state, LoadTemp(state, bytesFromList.ListTemp))),
            IrInst.BytesHash bytesHash => StoreTemp(state, bytesHash.Target, EmitBytesHash(state, LoadTemp(state, bytesHash.BytesTemp))),
            IrInst.BytesU16Le bytesU16Le => StoreTemp(state, bytesU16Le.Target, EmitBytesU16Le(state, LoadTemp(state, bytesU16Le.ValueTemp))),
            IrInst.BytesU32Le bytesU32Le => StoreTemp(state, bytesU32Le.Target, EmitBytesU32Le(state, LoadTemp(state, bytesU32Le.ValueTemp))),
            IrInst.BytesU64Le bytesU64Le => StoreTemp(state, bytesU64Le.Target, EmitBytesU64Le(state, LoadTemp(state, bytesU64Le.ValueTemp))),
            IrInst.BytesGetU16Le bytesGetU16Le => StoreTemp(state, bytesGetU16Le.Target, EmitBytesGetU16Le(state, LoadTemp(state, bytesGetU16Le.BytesTemp), LoadTemp(state, bytesGetU16Le.OffsetTemp))),
            IrInst.BytesGetU32Le bytesGetU32Le => StoreTemp(state, bytesGetU32Le.Target, EmitBytesGetU32Le(state, LoadTemp(state, bytesGetU32Le.BytesTemp), LoadTemp(state, bytesGetU32Le.OffsetTemp))),
            IrInst.BytesGetU64Le bytesGetU64Le => StoreTemp(state, bytesGetU64Le.Target, EmitBytesGetU64Le(state, LoadTemp(state, bytesGetU64Le.BytesTemp), LoadTemp(state, bytesGetU64Le.OffsetTemp))),
            IrInst.FileWriteBytes fileWriteBytes => StoreTemp(state, fileWriteBytes.Target, EmitFileWriteBytes(state, LoadTemp(state, fileWriteBytes.PathTemp), LoadTemp(state, fileWriteBytes.BytesTemp))),
            IrInst.ReadExact readExact => StoreTemp(state, readExact.Target, EmitReadExact(state, LoadTemp(state, readExact.CountTemp))),
            IrInst.TextByteLength textByteLength => StoreTemp(state, textByteLength.Target, EmitTextByteLength(state, LoadTemp(state, textByteLength.TextTemp))),
            IrInst.SpawnProcess spawnProcess => StoreTemp(state, spawnProcess.Target, EmitSpawnProcess(state, LoadTemp(state, spawnProcess.ExeTemp), LoadTemp(state, spawnProcess.ArgsTemp))),
            IrInst.ProcessWriteStdin procWriteStdin => StoreTemp(state, procWriteStdin.Target, EmitProcessWriteStdin(state, LoadTemp(state, procWriteStdin.ProcessTemp), LoadTemp(state, procWriteStdin.TextTemp))),
            IrInst.ProcessReadStdoutLine procReadStdout => StoreTemp(state, procReadStdout.Target, EmitProcessReadLine(state, LoadTemp(state, procReadStdout.ProcessTemp), stdoutFd: true)),
            IrInst.ProcessReadStderrLine procReadStderr => StoreTemp(state, procReadStderr.Target, EmitProcessReadLine(state, LoadTemp(state, procReadStderr.ProcessTemp), stdoutFd: false)),
            IrInst.ProcessWaitForExit procWait => StoreTemp(state, procWait.Target, EmitProcessWaitForExit(state, LoadTemp(state, procWait.ProcessTemp))),
            IrInst.ProcessKill procKill => StoreTemp(state, procKill.Target, EmitProcessKill(state, LoadTemp(state, procKill.ProcessTemp))),
            IrInst.CleanupResource cleanup => EmitResourceCleanup(state, LoadTemp(state, cleanup.SourceTemp), cleanup.TypeName),
            // Ordinary-value RC is not active yet. Arena restoration still owns reclamation.
            IrInst.RcDrop => false,
            _ => (bool?)null,
        };
    }

    private static bool? EmitInstructionGroup3(LlvmCodegenState state, IrInst instruction)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        return instruction switch
        {
            // Borrow: non-owning reference — simple value pass-through (pointer copy).
            // No ownership transfer, no drop responsibility. The owning scope still drops.
            IrInst.Borrow borrow => StoreTemp(state, borrow.Target, LoadTemp(state, borrow.SourceTemp)),
            // Erased Perceus marker: identity-preserving until runtime RC is enabled.
            IrInst.RcDup dup => StoreTemp(state, dup.Target, LoadTemp(state, dup.SourceTemp)),
            // CreateTask: allocate task struct with coroutine function + captures.
            IrInst.CreateTask createTask => StoreTemp(state, createTask.Target,
                EmitCreateTask(state, LoadTemp(state, createTask.ClosureTemp),
                    createTask.StateStructSize, createTask.CaptureCount, createTask.LoopResetEligible)),
            // CreateCompletedTask: pre-completed task with result already available.
            IrInst.CreateCompletedTask cct => StoreTemp(state, cct.Target,
                EmitCreateCompletedTask(state, LoadTemp(state, cct.ResultTemp))),
            // AwaitTask: should not appear after state machine transform. Pass-through for safety.
            IrInst.AwaitTask awaitTask => StoreTemp(state, awaitTask.Target, LoadTemp(state, awaitTask.TaskTemp)),
            // RunTask: drive task to completion through the run-queue scheduler (see
            // UseRunQueueScheduler); the legacy recursive driver remains for non-async task runs.
            IrInst.RunTask runTask => StoreTemp(state, runTask.Target,
                state.UseRunQueueScheduler
                    ? EmitNetworkingRuntimeCall(state, "ashes_scheduler_run", [LoadTemp(state, runTask.TaskTemp)], "run_sched")
                    : EmitRunTask(state, LoadTemp(state, runTask.TaskTemp))),
            // SpawnTask: detach a task (fire-and-forget); it advances while drivers wait.
            IrInst.SpawnTask spawnTask => StoreTemp(state, spawnTask.Target,
                EmitSpawnTask(state, LoadTemp(state, spawnTask.TaskTemp))),
            // Structured parallelism (Ashes.Task.Parallel.both).
            IrInst.ParallelFork parallelFork => StoreTemp(state, parallelFork.DescTarget,
                EmitParallelFork(state, LoadTemp(state, parallelFork.RightClosureTemp))),
            IrInst.ParallelJoin parallelJoin => StoreTemp(state, parallelJoin.ResultTarget,
                EmitParallelJoin(state, LoadTemp(state, parallelJoin.DescTemp))),
            IrInst.ParallelCleanup parallelCleanup =>
                EmitParallelCleanup(state, LoadTemp(state, parallelCleanup.DescTemp)),
            // withWorkers save/set/restore of the dynamically-scoped worker override global.
            IrInst.LoadParallelWorkerOverride loadOverride => StoreTemp(state, loadOverride.Target,
                LlvmApi.BuildLoad2(builder, state.I64,
                    LlvmApi.GetNamedGlobal(state.Target.Module, ParallelWorkerOverrideName), $"load_worker_override_{loadOverride.Target}")),
            IrInst.StoreParallelWorkerOverride storeOverride =>
                StoreParallelWorkerOverrideGlobal(state, LoadTemp(state, storeOverride.Source)),
            // Work-conserving parallel reduce (queued Ashes.Task.Parallel.reduce).
            IrInst.ParallelQueueStart parallelQueueStart => StoreTemp(state, parallelQueueStart.DescTarget,
                EmitParallelQueueStart(state, LoadTemp(state, parallelQueueStart.FClosureTemp),
                    LoadTemp(state, parallelQueueStart.CombineClosureTemp), LoadTemp(state, parallelQueueStart.ListTemp))),
            IrInst.ParallelQueueAwait parallelQueueAwait => StoreTemp(state, parallelQueueAwait.ResultTarget,
                EmitParallelQueueAwait(state, LoadTemp(state, parallelQueueAwait.DescTemp))),
            IrInst.ParallelQueueCleanup parallelQueueCleanup =>
                EmitParallelQueueCleanup(state, LoadTemp(state, parallelQueueCleanup.DescTemp)),
            _ => (bool?)null,
        };
    }

    private static bool? EmitInstructionGroup4(LlvmCodegenState state, IrInst instruction)
    {
        return instruction switch
        {
            // AsyncSleep: create a sleep task with a timer deadline.
            IrInst.AsyncSleep asyncSleep => StoreTemp(state, asyncSleep.Target,
                EmitAsyncSleep(state, LoadTemp(state, asyncSleep.MillisecondsTemp))),
            IrInst.CreateTcpConnectTask tcpConnectTask => StoreTemp(state, tcpConnectTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateTcpConnect, LoadTemp(state, tcpConnectTask.HostTemp), LoadTemp(state, tcpConnectTask.PortTemp), "tcp_connect_task")),
            IrInst.CreateTcpSendTask tcpSendTask => StoreTemp(state, tcpSendTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateTcpSend, LoadTemp(state, tcpSendTask.SocketTemp), LoadTemp(state, tcpSendTask.TextTemp), "tcp_send_task")),
            IrInst.CreateTcpReceiveTask tcpReceiveTask => StoreTemp(state, tcpReceiveTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateTcpReceive, LoadTemp(state, tcpReceiveTask.SocketTemp), LoadTemp(state, tcpReceiveTask.MaxBytesTemp), "tcp_receive_task")),
            IrInst.CreateTcpCloseTask tcpCloseTask => StoreTemp(state, tcpCloseTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateTcpClose, LoadTemp(state, tcpCloseTask.SocketTemp), LlvmApi.ConstInt(state.I64, 0, 0), "tcp_close_task")),
            IrInst.CreateTcpListenTask tcpListenTask => StoreTemp(state, tcpListenTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateTcpListen, LoadTemp(state, tcpListenTask.PortTemp), LlvmApi.ConstInt(state.I64, 0, 0), "tcp_listen_task")),
            IrInst.CreateForkWorkersTask forkWorkersTask => StoreTemp(state, forkWorkersTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateForkWorkers, LoadTemp(state, forkWorkersTask.PortTemp), LoadTemp(state, forkWorkersTask.CountTemp), "fork_workers_task")),
            // Synchronous setter: store the drain bound (ms) for this process; yields unit.
            IrInst.SetDrainTimeout setDrainTimeout => StoreTemp(state, setDrainTimeout.Target,
                EmitSetDrainTimeout(state, LoadTemp(state, setDrainTimeout.MsTemp))),
            // Stop.stop: request graceful whole-server shutdown; yields unit.
            IrInst.RequestServerStop requestServerStop => StoreTemp(state, requestServerStop.Target,
                EmitRequestServerStop(state)),
            IrInst.CreateTcpAcceptTask tcpAcceptTask => StoreTemp(state, tcpAcceptTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateTcpAccept, LoadTemp(state, tcpAcceptTask.SocketTemp), LlvmApi.ConstInt(state.I64, 0, 0), "tcp_accept_task")),
            IrInst.CreateHttpGetTask httpGetTask => StoreTemp(state, httpGetTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateHttpGet, LoadTemp(state, httpGetTask.UrlTemp), LlvmApi.ConstInt(state.I64, 0, 0), "http_get_task")),
            IrInst.CreateHttpPostTask httpPostTask => StoreTemp(state, httpPostTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateHttpPost, LoadTemp(state, httpPostTask.UrlTemp), LoadTemp(state, httpPostTask.BodyTemp), "http_post_task")),
            IrInst.CreateTlsConnectTask tlsConnectTask => StoreTemp(state, tlsConnectTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateTlsConnect, LoadTemp(state, tlsConnectTask.HostTemp), LoadTemp(state, tlsConnectTask.PortTemp), "tls_connect_task")),
            IrInst.CreateTlsHandshakeTask tlsHandshakeTask => StoreTemp(state, tlsHandshakeTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateTlsHandshake, LoadTemp(state, tlsHandshakeTask.SocketTemp), LoadTemp(state, tlsHandshakeTask.HostTemp), "tls_handshake_task")),
            IrInst.CreateTlsServerHandshakeTask tlsServerHandshakeTask => StoreTemp(state, tlsServerHandshakeTask.Target,
                EmitCreateTlsServerHandshakeTask(state, LoadTemp(state, tlsServerHandshakeTask.SocketTemp), LoadTemp(state, tlsServerHandshakeTask.CertTemp), LoadTemp(state, tlsServerHandshakeTask.KeyTemp))),
            IrInst.CreateTlsSendTask tlsSendTask => StoreTemp(state, tlsSendTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateTlsSend, LoadTemp(state, tlsSendTask.SslTemp), LoadTemp(state, tlsSendTask.TextTemp), "tls_send_task")),
            IrInst.CreateTlsReceiveTask tlsReceiveTask => StoreTemp(state, tlsReceiveTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateTlsReceive, LoadTemp(state, tlsReceiveTask.SslTemp), LoadTemp(state, tlsReceiveTask.MaxBytesTemp), "tls_receive_task")),
            IrInst.CreateTlsCloseTask tlsCloseTask => StoreTemp(state, tlsCloseTask.Target,
                EmitCreateLeafNetworkingTask(state, TaskStructLayout.StateTlsClose, LoadTemp(state, tlsCloseTask.SslTemp), LlvmApi.ConstInt(state.I64, 0, 0), "tls_close_task")),
            _ => (bool?)null,
        };
    }

    private static bool? EmitInstructionGroup5(LlvmCodegenState state, IrInst instruction)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        return instruction switch
        {
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
            // Capabilities: dynamically-scoped handler evidence in per-capability module globals.
            IrInst.LoadCapabilityHandler loadCapabilityHandler => StoreTemp(state, loadCapabilityHandler.Target,
                LlvmApi.BuildLoad2(builder, state.I64, GetCapabilityHandlerGlobal(state, loadCapabilityHandler.CapabilityIndex), $"load_capability_{loadCapabilityHandler.CapabilityIndex}")),
            IrInst.StoreCapabilityHandler storeCapabilityHandler =>
                StoreCapabilityHandlerGlobal(state, storeCapabilityHandler.CapabilityIndex, LoadTemp(state, storeCapabilityHandler.Source)),
            IrInst.LoadLocal loadLocal => StoreTemp(state, loadLocal.Target, LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[loadLocal.Slot], $"load_local_{loadLocal.Slot}")),
            IrInst.StoreLocal storeLocal => StoreLocal(state, storeLocal.Slot, LoadTemp(state, storeLocal.Source)),
            IrInst.LoadEnv loadEnv => StoreTemp(state, loadEnv.Target, LlvmApi.BuildLoad2(builder, state.I64, GetMemoryPointer(state, LlvmApi.BuildLoad2(builder, state.I64, state.LocalSlots[0], "env_ptr"), loadEnv.Index * 8, $"load_env_{loadEnv.Index}_ptr"), $"load_env_{loadEnv.Index}")),
            IrInst.Alloc alloc => StoreTemp(state, alloc.Target, EmitAlloc(state, alloc.SizeBytes)),
            IrInst.AllocStack allocStack => StoreTemp(state, allocStack.Target, EmitStackAlloc(state, allocStack.SizeBytes, $"stack_alloc_{allocStack.Target}")),
            IrInst.AddInt addInt => StoreTemp(state, addInt.Target, LlvmApi.BuildAdd(builder, LoadTemp(state, addInt.Left), LoadTemp(state, addInt.Right), $"add_{addInt.Target}")),
            IrInst.AddFloat addFloat => StoreTemp(state, addFloat.Target, LlvmApi.BuildFAdd(builder, LoadTempAsFloat(state, addFloat.Left), LoadTempAsFloat(state, addFloat.Right), $"fadd_{addFloat.Target}")),
            IrInst.SubInt subInt => StoreTemp(state, subInt.Target, LlvmApi.BuildSub(builder, LoadTemp(state, subInt.Left), LoadTemp(state, subInt.Right), $"sub_{subInt.Target}")),
            IrInst.SubFloat subFloat => StoreTemp(state, subFloat.Target, LlvmApi.BuildFSub(builder, LoadTempAsFloat(state, subFloat.Left), LoadTempAsFloat(state, subFloat.Right), $"fsub_{subFloat.Target}")),
            IrInst.MulInt mulInt => StoreTemp(state, mulInt.Target, LlvmApi.BuildMul(builder, LoadTemp(state, mulInt.Left), LoadTemp(state, mulInt.Right), $"mul_{mulInt.Target}")),
            IrInst.MulFloat mulFloat => StoreTemp(state, mulFloat.Target, LlvmApi.BuildFMul(builder, LoadTempAsFloat(state, mulFloat.Left), LoadTempAsFloat(state, mulFloat.Right), $"fmul_{mulFloat.Target}")),
            IrInst.DivInt divInt => StoreTemp(state, divInt.Target, LlvmApi.BuildSDiv(builder, LoadTemp(state, divInt.Left), LoadTemp(state, divInt.Right), $"div_{divInt.Target}")),
            IrInst.DivUInt divUInt => StoreTemp(state, divUInt.Target, LlvmApi.BuildUDiv(builder, LoadTemp(state, divUInt.Left), LoadTemp(state, divUInt.Right), $"udiv_{divUInt.Target}")),
            IrInst.DivFloat divFloat => StoreTemp(state, divFloat.Target, LlvmApi.BuildFDiv(builder, LoadTempAsFloat(state, divFloat.Left), LoadTempAsFloat(state, divFloat.Right), $"fdiv_{divFloat.Target}")),
            IrInst.IntToFloat intToFloat => StoreTemp(state, intToFloat.Target, LlvmApi.BuildSIToFP(builder, LoadTemp(state, intToFloat.ValueTemp), state.F64, $"sitofp_{intToFloat.Target}")),
            IrInst.FloatToInt floatToInt => StoreTemp(state, floatToInt.Target, LlvmApi.BuildFPToSI(builder, LoadTempAsFloat(state, floatToInt.ValueTemp), state.I64, $"fptosi_{floatToInt.Target}")),
            IrInst.FloatUnaryIntrinsic floatUnary => StoreTemp(state, floatUnary.Target, EmitFloatUnaryIntrinsic(state, LoadTempAsFloat(state, floatUnary.ValueTemp), floatUnary.LlvmIntrinsic)),
            IrInst.CallLibm callLibm => StoreTemp(state, callLibm.Target, EmitCallLibm(state, callLibm.Symbol, callLibm.Args)),
            IrInst.RegexCompile regexCompile => StoreTemp(state, regexCompile.Target, EmitRegexCompile(state, LoadTemp(state, regexCompile.Pattern))),
            IrInst.RegexCompileError regexCompileError => StoreTemp(state, regexCompileError.Target, EmitRegexCompileError(state, LoadTemp(state, regexCompileError.Pattern))),
            IrInst.RegexFind regexFind => StoreTemp(state, regexFind.Target, EmitRegexFind(state, LoadTemp(state, regexFind.Code), LoadTemp(state, regexFind.Subject), LoadTemp(state, regexFind.Start))),
            IrInst.RegexCaptures regexCaptures => StoreTemp(state, regexCaptures.Target, EmitRegexCaptures(state, LoadTemp(state, regexCaptures.Code), LoadTemp(state, regexCaptures.Subject), LoadTemp(state, regexCaptures.Start))),
            IrInst.RegexSubstitute regexSubstitute => StoreTemp(state, regexSubstitute.Target, EmitRegexSubstitute(state, LoadTemp(state, regexSubstitute.Code), LoadTemp(state, regexSubstitute.Subject), LoadTemp(state, regexSubstitute.Replacement))),
            IrInst.AndInt andInt => StoreTemp(state, andInt.Target, LlvmApi.BuildAnd(builder, LoadTemp(state, andInt.Left), LoadTemp(state, andInt.Right), $"and_{andInt.Target}")),
            IrInst.OrInt orInt => StoreTemp(state, orInt.Target, LlvmApi.BuildOr(builder, LoadTemp(state, orInt.Left), LoadTemp(state, orInt.Right), $"or_{orInt.Target}")),
            IrInst.XorInt xorInt => StoreTemp(state, xorInt.Target, LlvmApi.BuildXor(builder, LoadTemp(state, xorInt.Left), LoadTemp(state, xorInt.Right), $"xor_{xorInt.Target}")),
            _ => (bool?)null,
        };
    }

    private static bool? EmitInstructionGroup6(LlvmCodegenState state, IrInst instruction, int index, IReadOnlyList<IrInst> instructions)
    {
        return instruction switch
        {
            IrInst.ShlInt shlInt => StoreTemp(state, shlInt.Target, EmitShiftInt(state, LoadTemp(state, shlInt.Left), LoadTemp(state, shlInt.Right), left: true, name: $"shl_{shlInt.Target}")),
            IrInst.ShrInt shrInt => StoreTemp(state, shrInt.Target, EmitShiftInt(state, LoadTemp(state, shrInt.Left), LoadTemp(state, shrInt.Right), left: false, name: $"shr_{shrInt.Target}")),
            IrInst.CmpIntGt cmpIntGt => StoreTemp(state, cmpIntGt.Target, EmitIntComparison(state, LlvmIntPredicate.Sgt, LoadTemp(state, cmpIntGt.Left), LoadTemp(state, cmpIntGt.Right), $"cmp_gt_{cmpIntGt.Target}")),
            IrInst.CmpUIntGt cmpUIntGt => StoreTemp(state, cmpUIntGt.Target, EmitIntComparison(state, LlvmIntPredicate.Ugt, LoadTemp(state, cmpUIntGt.Left), LoadTemp(state, cmpUIntGt.Right), $"cmp_gt_{cmpUIntGt.Target}")),
            IrInst.CmpFloatGt cmpFloatGt => StoreTemp(state, cmpFloatGt.Target, EmitFloatComparison(state, LlvmRealPredicate.Ogt, LoadTempAsFloat(state, cmpFloatGt.Left), LoadTempAsFloat(state, cmpFloatGt.Right), $"fcmp_gt_{cmpFloatGt.Target}")),
            IrInst.CmpIntGe cmpIntGe => StoreTemp(state, cmpIntGe.Target, EmitIntComparison(state, LlvmIntPredicate.Sge, LoadTemp(state, cmpIntGe.Left), LoadTemp(state, cmpIntGe.Right), $"cmp_ge_{cmpIntGe.Target}")),
            IrInst.CmpUIntGe cmpUIntGe => StoreTemp(state, cmpUIntGe.Target, EmitIntComparison(state, LlvmIntPredicate.Uge, LoadTemp(state, cmpUIntGe.Left), LoadTemp(state, cmpUIntGe.Right), $"cmp_ge_{cmpUIntGe.Target}")),
            IrInst.CmpFloatGe cmpFloatGe => StoreTemp(state, cmpFloatGe.Target, EmitFloatComparison(state, LlvmRealPredicate.Oge, LoadTempAsFloat(state, cmpFloatGe.Left), LoadTempAsFloat(state, cmpFloatGe.Right), $"fcmp_ge_{cmpFloatGe.Target}")),
            IrInst.CmpIntLt cmpIntLt => StoreTemp(state, cmpIntLt.Target, EmitIntComparison(state, LlvmIntPredicate.Slt, LoadTemp(state, cmpIntLt.Left), LoadTemp(state, cmpIntLt.Right), $"cmp_lt_{cmpIntLt.Target}")),
            IrInst.CmpUIntLt cmpUIntLt => StoreTemp(state, cmpUIntLt.Target, EmitIntComparison(state, LlvmIntPredicate.Ult, LoadTemp(state, cmpUIntLt.Left), LoadTemp(state, cmpUIntLt.Right), $"cmp_lt_{cmpUIntLt.Target}")),
            IrInst.CmpFloatLt cmpFloatLt => StoreTemp(state, cmpFloatLt.Target, EmitFloatComparison(state, LlvmRealPredicate.Olt, LoadTempAsFloat(state, cmpFloatLt.Left), LoadTempAsFloat(state, cmpFloatLt.Right), $"fcmp_lt_{cmpFloatLt.Target}")),
            IrInst.CmpIntLe cmpIntLe => StoreTemp(state, cmpIntLe.Target, EmitIntComparison(state, LlvmIntPredicate.Sle, LoadTemp(state, cmpIntLe.Left), LoadTemp(state, cmpIntLe.Right), $"cmp_le_{cmpIntLe.Target}")),
            IrInst.CmpUIntLe cmpUIntLe => StoreTemp(state, cmpUIntLe.Target, EmitIntComparison(state, LlvmIntPredicate.Ule, LoadTemp(state, cmpUIntLe.Left), LoadTemp(state, cmpUIntLe.Right), $"cmp_le_{cmpUIntLe.Target}")),
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
            IrInst.ConcatStrTip concatStrTip => StoreTemp(state, concatStrTip.Target, EmitConcatStrTip(state, LoadTemp(state, concatStrTip.Left), LoadTemp(state, concatStrTip.Right), concatStrTip.ResvStartSlot, concatStrTip.ResvEndSlot)),
            IrInst.MakeClosure makeClosure => StoreTemp(state, makeClosure.Target, EmitMakeClosure(state, makeClosure.FuncLabel, LoadTemp(state, makeClosure.EnvPtrTemp), makeClosure.EnvSizeBytes)),
            IrInst.LoadFuncAddr loadFuncAddr => StoreTemp(state, loadFuncAddr.Target,
                LlvmApi.BuildPtrToInt(state.Target.Builder, state.LiftedFunctions[loadFuncAddr.FuncLabel], state.I64, $"func_addr_{loadFuncAddr.FuncLabel}")),
            IrInst.MakeClosureStack makeClosureStack => StoreTemp(state, makeClosureStack.Target, EmitMakeClosureStack(state, makeClosureStack.FuncLabel, LoadTemp(state, makeClosureStack.EnvPtrTemp), makeClosureStack.EnvSizeBytes)),
            IrInst.CallClosure callClosure => StoreTemp(state, callClosure.Target, EmitCallClosure(state, LoadTemp(state, callClosure.ClosureTemp), LoadTemp(state, callClosure.ArgTemp),
                isTailCall: index + 1 < instructions.Count && instructions[index + 1] is IrInst.Return ret && ret.Source == callClosure.Target)),
            IrInst.CallKnown callKnown => StoreTemp(state, callKnown.Target, EmitCallKnown(state, callKnown.FuncLabel, LoadTemp(state, callKnown.EnvTemp), LoadTemp(state, callKnown.ArgTemp),
                isTailCall: index + 1 < instructions.Count && instructions[index + 1] is IrInst.Return retK && retK.Source == callKnown.Target)),
            IrInst.ToCString toCString => StoreTemp(state, toCString.Target, EmitToCString(state, LoadTemp(state, toCString.StrTemp))),
            _ => (bool?)null,
        };
    }

    private static bool? EmitInstructionGroup7(LlvmCodegenState state, IrInst instruction, int index)
    {
        return instruction switch
        {
            IrInst.CallExternal callExternal => StoreTemp(state, callExternal.Target, EmitCallExternal(state, callExternal.SymbolName, callExternal.LibraryName, callExternal.ArgTemps, callExternal.ParameterTypes, callExternal.ReturnType)),
            IrInst.LoadMemOffset loadMemOffset => StoreTemp(state, loadMemOffset.Target, LoadMemory(state, LoadTemp(state, loadMemOffset.BasePtr), loadMemOffset.OffsetBytes, $"load_mem_{loadMemOffset.Target}")),
            IrInst.StoreMemOffset storeMemOffset => StoreMemory(state, LoadTemp(state, storeMemOffset.BasePtr), storeMemOffset.OffsetBytes, LoadTemp(state, storeMemOffset.Source), $"store_mem_{storeMemOffset.OffsetBytes}"),
            IrInst.AllocAdt allocAdt => StoreTemp(state, allocAdt.Target, EmitAllocAdt(state, allocAdt.Tag, allocAdt.FieldCount)),
            IrInst.AllocAdtToSpace allocToSpace => StoreTemp(state, allocToSpace.Target, EmitAllocAdtToSpace(state, allocToSpace.Tag, allocToSpace.FieldCount)),
            IrInst.AllocReusing allocReusing => StoreTemp(state, allocReusing.Target, EmitAllocReusing(state, LoadTemp(state, allocReusing.TokenTemp), allocReusing.Tag)),
            IrInst.AllocAdtStack allocAdtStack => StoreTemp(state, allocAdtStack.Target, EmitStackAllocAdt(state, allocAdtStack.Tag, allocAdtStack.FieldCount)),
            IrInst.SetAdtField setAdtField => StoreAdtField(state, LoadTemp(state, setAdtField.Ptr), setAdtField.FieldIndex, LoadTemp(state, setAdtField.Source), $"set_adt_field_{setAdtField.FieldIndex}"),
            IrInst.SaveStackPointer saveSp => EmitSaveStackPointer(state, saveSp.Slot),
            IrInst.RestoreStackPointer restoreSp => EmitRestoreStackPointer(state, restoreSp.Slot),
            IrInst.GetAdtTag getAdtTag => StoreTemp(state, getAdtTag.Target, LoadAdtTag(state, LoadTemp(state, getAdtTag.Ptr), $"get_adt_tag_{getAdtTag.Target}")),
            IrInst.GetAdtField getAdtField => StoreTemp(state, getAdtField.Target, LoadAdtField(state, LoadTemp(state, getAdtField.Ptr), getAdtField.FieldIndex, $"get_adt_field_{getAdtField.Target}")),
            IrInst.Jump jump => EmitJump(state, jump.Target),
            IrInst.JumpIfFalse jumpIfFalse => EmitJumpIfFalse(state, LoadTemp(state, jumpIfFalse.CondTemp), jumpIfFalse.Target, index),
            IrInst.SwitchTag switchTag => EmitSwitchTag(state, LoadTemp(state, switchTag.TagTemp), switchTag.Cases, switchTag.DefaultLabel),
            IrInst.Return ret => EmitReturn(state, ret.Source),
            // Arena deallocation: save/restore heap cursor and end pointers
            IrInst.SaveArenaState save => EmitSaveArenaState(state, save.CursorLocalSlot, save.EndLocalSlot, save.CoroutineLoop),
            IrInst.RestoreArenaState restore => EmitRestoreArenaState(state, restore.CursorLocalSlot, restore.EndLocalSlot, restore.PreRestoreEndSlot, restore.CoroutineLoop),
            IrInst.ReclaimArenaChunks reclaim => EmitReclaimArenaChunks(state, reclaim.SavedEndSlot, reclaim.PreRestoreEndSlot, reclaim.CoroutineLoop),
            IrInst.CopyOutArena copyOut => StoreTemp(state, copyOut.DestTemp, EmitCopyOutArena(state, copyOut.SrcTemp, copyOut.StaticSizeBytes)),
            IrInst.CopyOutArenaToSpace copyOutTs => StoreTemp(state, copyOutTs.DestTemp, EmitCopyOutArenaToSpace(state, copyOutTs.SrcTemp, copyOutTs.StaticSizeBytes)),
            IrInst.CopyFixedInto copyInto => EmitCopyFixedInto(state, copyInto.DestTemp, copyInto.SrcTemp, copyInto.SizeBytes),
            IrInst.CopyStringIntoOrFresh copyStr => StoreTemp(state, copyStr.DestTemp, EmitCopyStringIntoOrFresh(state, copyStr.OldBlobTemp, copyStr.SrcTemp)),
            IrInst.CopyFixedIntoOrFresh copyFixed => StoreTemp(state, copyFixed.DestTemp, EmitCopyFixedIntoOrFresh(state, copyFixed.OldBlobTemp, copyFixed.SrcTemp, copyFixed.SizeBytes)),
            IrInst.CopyOutList copyOutList => StoreTemp(state, copyOutList.DestTemp, EmitCopyOutList(state, copyOutList.SrcTemp, copyOutList.HeadCopy)),
            IrInst.CopyOutClosure copyOutClosure => StoreTemp(state, copyOutClosure.DestTemp, EmitCopyOutClosure(state, copyOutClosure.SrcTemp)),
            IrInst.CopyOutTcoListCell tcoCell => StoreTemp(state, tcoCell.DestTemp, EmitCopyOutTcoListCell(state, tcoCell.SrcTemp, tcoCell.HeadCopy)),
            _ => (bool?)null,
        };
    }

    private static LlvmValueHandle GetCapabilityHandlerGlobal(LlvmCodegenState state, int capabilityIndex)
    {
        return LlvmApi.GetNamedGlobal(state.Target.Module, $"__ashes_capability_handler_{capabilityIndex}");
    }

    private static bool StoreCapabilityHandlerGlobal(LlvmCodegenState state, int capabilityIndex, LlvmValueHandle value)
    {
        LlvmApi.BuildStore(state.Target.Builder, value, GetCapabilityHandlerGlobal(state, capabilityIndex));
        return false;
    }

    private static bool StoreParallelWorkerOverrideGlobal(LlvmCodegenState state, LlvmValueHandle value)
    {
        LlvmApi.BuildStore(state.Target.Builder, value,
            LlvmApi.GetNamedGlobal(state.Target.Module, ParallelWorkerOverrideName));
        return false;
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
        LlvmTypeHandle F32,
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
        LlvmValueHandle WindowsIoctlSocketImport,
        LlvmValueHandle WindowsWsaGetLastErrorImport,
        LlvmValueHandle WindowsWsaPollImport,
        LlvmValueHandle WindowsLoadLibraryImport,
        LlvmValueHandle WindowsGetProcAddressImport,
        LlvmValueHandle WindowsCertOpenSystemStoreImport,
        LlvmValueHandle WindowsCertEnumCertificatesInStoreImport,
        LlvmValueHandle WindowsCertCloseStoreImport,
        LlvmValueHandle WindowsBindImport,
        LlvmValueHandle WindowsSetSockOptImport,
        LlvmValueHandle WindowsWsaIoctlImport,
        LlvmValueHandle WindowsWsaSendImport,
        LlvmValueHandle WindowsWsaRecvImport,
        LlvmValueHandle WindowsCreateIoCompletionPortImport,
        LlvmValueHandle WindowsGetQueuedCompletionStatusImport,
        LlvmValueHandle WindowsIocpPortGlobal,
        LlvmValueHandle WindowsExitProcessImport,
        LlvmValueHandle WindowsGetCommandLineImport,
        LlvmValueHandle WindowsWideCharToMultiByteImport,
        LlvmValueHandle WindowsLocalFreeImport,
        LlvmValueHandle WindowsCommandLineToArgvImport,
        LlvmValueHandle WindowsSleepImport,
        LlvmValueHandle WindowsVirtualAllocImport,
        LlvmValueHandle WindowsVirtualFreeImport,
        LlvmValueHandle WindowsCreatePipeImport,
        LlvmValueHandle WindowsCreateProcessAImport,
        LlvmValueHandle WindowsTerminateProcessImport,
        LlvmValueHandle WindowsWaitForSingleObjectImport,
        LlvmValueHandle WindowsGetExitCodeProcessImport,
        Dictionary<string, LlvmValueHandle> WindowsExternalImports,
        LlvmCodegenFlavor Flavor,
        bool UsesProgramArgs,
        bool IsEntry)
    {
        // Persistent "to-space" arena cursor/end (i64 slots), parallel to HeapCursorSlot/HeapEndSlot.
        // Used by AllocAdtToSpace for genuinely-new cells in in-place reuse specializations. Lazily
        // initialized: both start at 0, so the first allocation's `cursor + size > end` check grows the
        // first chunk. Never reset by the TCO back-edge, so reused-loop inserts survive the reset.
        public LlvmValueHandle ToSpaceCursorSlot { get; init; }
        public LlvmValueHandle ToSpaceEndSlot { get; init; }
        public LlvmValueHandle BlobCursorSlot { get; init; }
        public LlvmValueHandle BlobEndSlot { get; init; }

        // When true, RunTask drives the top-level task through the run-queue scheduler
        // (ashes_scheduler_run) instead of the legacy recursive driver. Set for every async program
        // (any RunTask use) on all targets.
        public bool UseRunQueueScheduler { get; init; }

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
        WindowsX64,
        WindowsArm64
    }

    private static bool IsLinuxFlavor(LlvmCodegenFlavor flavor) =>
        flavor is LlvmCodegenFlavor.LinuxX64 or LlvmCodegenFlavor.LinuxArm64;

    private static bool IsLinuxArm64Flavor(LlvmCodegenFlavor flavor) =>
        flavor == LlvmCodegenFlavor.LinuxArm64;

    private static bool IsWindowsFlavor(LlvmCodegenFlavor flavor) =>
        flavor is LlvmCodegenFlavor.WindowsX64 or LlvmCodegenFlavor.WindowsArm64;

    // Windows-on-ARM64: AArch64 instruction/register model with the Windows PE/import ABI.
    private static bool IsArm64Flavor(LlvmCodegenFlavor flavor) =>
        flavor is LlvmCodegenFlavor.LinuxArm64 or LlvmCodegenFlavor.WindowsArm64;

    /// <summary>
    /// Emits local <c>memcpy</c> and <c>memset</c> function implementations so that LLVM's
    /// intrinsic lowering has definitions to call. Without libc, the linker would fail
    /// on the external symbols, so these definitions are added directly to the module.
    /// </summary>
    private static void EmitBuiltinMemcpy(
        LlvmTargetContext target, LlvmTypeHandle i8, LlvmTypeHandle i64, LlvmTypeHandle i8Ptr)
    {
        EmitBuiltinMemcpyMemcpy(target, i8, i64, i8Ptr);
        EmitBuiltinMemcpyMemset(target, i8, i64, i8Ptr);
        EmitBuiltinMemcpyStrlen(target, i8, i64, i8Ptr);
    }

    // memcpy(dest, src, n) → dest
    private static void EmitBuiltinMemcpyMemcpy(
        LlvmTargetContext target, LlvmTypeHandle i8, LlvmTypeHandle i64, LlvmTypeHandle i8Ptr)
    {
        LlvmTypeHandle memcpyType = LlvmApi.FunctionType(i8Ptr, [i8Ptr, i8Ptr, i64]);
        LlvmValueHandle fn = LlvmApi.AddFunction(target.Module, "memcpy", memcpyType);
        ApplyBuiltinAttributes(target, fn, isReadOnly: false, destSrcNoAlias: true, returnsPointer: true, pointerParamCount: 2);

        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "entry");
        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "check");
        LlvmBasicBlockHandle bodyBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "body");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "done");

        LlvmValueHandle dest = LlvmApi.GetParam(fn, 0);
        LlvmValueHandle src = LlvmApi.GetParam(fn, 1);
        LlvmValueHandle size = LlvmApi.GetParam(fn, 2);

        LlvmApi.PositionBuilderAtEnd(target.Builder, entry);
        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(target.Builder, i64, "idx");
        LlvmApi.BuildStore(target.Builder, LlvmApi.ConstInt(i64, 0, 0), idxSlot);
        LlvmApi.BuildBr(target.Builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(target.Builder, checkBlock);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(target.Builder, i64, idxSlot, "i");
        LlvmValueHandle cond = LlvmApi.BuildICmp(target.Builder, LlvmIntPredicate.Ult, idx, size, "cmp");
        LlvmApi.BuildCondBr(target.Builder, cond, bodyBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(target.Builder, bodyBlock);
        LlvmValueHandle srcPtr = LlvmApi.BuildGEP2(target.Builder, i8, src, [idx], "src_ptr");
        LlvmValueHandle dstPtr = LlvmApi.BuildGEP2(target.Builder, i8, dest, [idx], "dst_ptr");
        LlvmValueHandle val = LlvmApi.BuildLoad2(target.Builder, i8, srcPtr, "byte");
        LlvmApi.BuildStore(target.Builder, val, dstPtr);
        LlvmValueHandle nextIdx = LlvmApi.BuildAdd(target.Builder, idx, LlvmApi.ConstInt(i64, 1, 0), "next");
        LlvmApi.BuildStore(target.Builder, nextIdx, idxSlot);
        LlvmApi.BuildBr(target.Builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(target.Builder, doneBlock);
        LlvmApi.BuildRet(target.Builder, dest);
    }

    // memset(dest, val, n) → dest
    private static void EmitBuiltinMemcpyMemset(
        LlvmTargetContext target, LlvmTypeHandle i8, LlvmTypeHandle i64, LlvmTypeHandle i8Ptr)
    {
        LlvmTypeHandle i32 = LlvmApi.Int32TypeInContext(target.Context);
        LlvmTypeHandle memsetType = LlvmApi.FunctionType(i8Ptr, [i8Ptr, i32, i64]);
        LlvmValueHandle fn = LlvmApi.AddFunction(target.Module, "memset", memsetType);
        // memset writes to dest only; dest pointer is nonnull. Returns dest pointer.
        ApplyBuiltinAttributes(target, fn, isReadOnly: false, returnsPointer: true, pointerParamCount: 1);

        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "entry");
        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "check");
        LlvmBasicBlockHandle bodyBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "body");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "done");

        LlvmValueHandle dest = LlvmApi.GetParam(fn, 0);
        LlvmValueHandle fillVal = LlvmApi.GetParam(fn, 1);
        LlvmValueHandle size = LlvmApi.GetParam(fn, 2);

        LlvmApi.PositionBuilderAtEnd(target.Builder, entry);
        LlvmValueHandle fillByte = LlvmApi.BuildTrunc(target.Builder, fillVal, i8, "fill_byte");
        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(target.Builder, i64, "idx");
        LlvmApi.BuildStore(target.Builder, LlvmApi.ConstInt(i64, 0, 0), idxSlot);
        LlvmApi.BuildBr(target.Builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(target.Builder, checkBlock);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(target.Builder, i64, idxSlot, "i");
        LlvmValueHandle cond = LlvmApi.BuildICmp(target.Builder, LlvmIntPredicate.Ult, idx, size, "cmp");
        LlvmApi.BuildCondBr(target.Builder, cond, bodyBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(target.Builder, bodyBlock);
        LlvmValueHandle dstPtr = LlvmApi.BuildGEP2(target.Builder, i8, dest, [idx], "dst_ptr");
        LlvmApi.BuildStore(target.Builder, fillByte, dstPtr);
        LlvmValueHandle nextIdx = LlvmApi.BuildAdd(target.Builder, idx, LlvmApi.ConstInt(i64, 1, 0), "next");
        LlvmApi.BuildStore(target.Builder, nextIdx, idxSlot);
        LlvmApi.BuildBr(target.Builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(target.Builder, doneBlock);
        LlvmApi.BuildRet(target.Builder, dest);
    }

    // strlen(s) → length
    private static void EmitBuiltinMemcpyStrlen(
        LlvmTargetContext target, LlvmTypeHandle i8, LlvmTypeHandle i64, LlvmTypeHandle i8Ptr)
    {
        LlvmTypeHandle strlenType = LlvmApi.FunctionType(i64, [i8Ptr]);
        LlvmValueHandle fn = LlvmApi.AddFunction(target.Module, "strlen", strlenType);
        ApplyBuiltinAttributes(target, fn, isReadOnly: true, pointerParamCount: 1);

        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "entry");
        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "check");
        LlvmBasicBlockHandle bodyBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "body");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "done");

        LlvmValueHandle str = LlvmApi.GetParam(fn, 0);

        LlvmApi.PositionBuilderAtEnd(target.Builder, entry);
        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(target.Builder, i64, "idx");
        LlvmApi.BuildStore(target.Builder, LlvmApi.ConstInt(i64, 0, 0), idxSlot);
        LlvmApi.BuildBr(target.Builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(target.Builder, checkBlock);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(target.Builder, i64, idxSlot, "i");
        LlvmValueHandle charPtr = LlvmApi.BuildGEP2(target.Builder, i8, str, [idx], "char_ptr");
        LlvmValueHandle ch = LlvmApi.BuildLoad2(target.Builder, i8, charPtr, "ch");
        LlvmValueHandle isNull = LlvmApi.BuildICmp(target.Builder, LlvmIntPredicate.Eq, ch, LlvmApi.ConstInt(i8, 0, 0), "is_null");
        LlvmApi.BuildCondBr(target.Builder, isNull, doneBlock, bodyBlock);

        LlvmApi.PositionBuilderAtEnd(target.Builder, bodyBlock);
        LlvmValueHandle nextIdx = LlvmApi.BuildAdd(target.Builder, idx, LlvmApi.ConstInt(i64, 1, 0), "next");
        LlvmApi.BuildStore(target.Builder, nextIdx, idxSlot);
        LlvmApi.BuildBr(target.Builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(target.Builder, doneBlock);
        LlvmValueHandle finalIdx = LlvmApi.BuildLoad2(target.Builder, i64, idxSlot, "len");
        LlvmApi.BuildRet(target.Builder, finalIdx);
    }

    /// <summary>
    /// Emits a freestanding <c>memcmp(a, b, n)</c> → <c>int</c> implementation.
    /// Returns 0 when the byte ranges are equal, non-zero otherwise.
    /// Used to replace byte-by-byte comparison loops in string equality checks.
    /// </summary>
    private static void EmitBuiltinMemcmp(
        LlvmTargetContext target, LlvmTypeHandle i8, LlvmTypeHandle i64, LlvmTypeHandle i8Ptr)
    {
        LlvmTypeHandle i32 = LlvmApi.Int32TypeInContext(target.Context);
        LlvmTypeHandle memcmpType = LlvmApi.FunctionType(i32, [i8Ptr, i8Ptr, i64]);
        LlvmValueHandle fn = LlvmApi.AddFunction(target.Module, "memcmp", memcmpType);
        ApplyBuiltinAttributes(target, fn, isReadOnly: true, pointerParamCount: 2);

        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "entry");
        LlvmBasicBlockHandle checkBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "check");
        LlvmBasicBlockHandle bodyBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "body");
        LlvmBasicBlockHandle neBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "not_equal");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "done");

        LlvmValueHandle ptrA = LlvmApi.GetParam(fn, 0);
        LlvmValueHandle ptrB = LlvmApi.GetParam(fn, 1);
        LlvmValueHandle size = LlvmApi.GetParam(fn, 2);

        // entry: idx = 0; goto check
        LlvmApi.PositionBuilderAtEnd(target.Builder, entry);
        LlvmValueHandle idxSlot = LlvmApi.BuildAlloca(target.Builder, i64, "idx");
        LlvmApi.BuildStore(target.Builder, LlvmApi.ConstInt(i64, 0, 0), idxSlot);
        LlvmApi.BuildBr(target.Builder, checkBlock);

        // check: if idx < size goto body else goto done (equal)
        LlvmApi.PositionBuilderAtEnd(target.Builder, checkBlock);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(target.Builder, i64, idxSlot, "i");
        LlvmValueHandle cond = LlvmApi.BuildICmp(target.Builder, LlvmIntPredicate.Ult, idx, size, "cmp");
        LlvmApi.BuildCondBr(target.Builder, cond, bodyBlock, doneBlock);

        // body: compare bytes at [a+idx] vs [b+idx]
        LlvmApi.PositionBuilderAtEnd(target.Builder, bodyBlock);
        LlvmValueHandle aPtr = LlvmApi.BuildGEP2(target.Builder, i8, ptrA, [idx], "a_ptr");
        LlvmValueHandle bPtr = LlvmApi.BuildGEP2(target.Builder, i8, ptrB, [idx], "b_ptr");
        LlvmValueHandle aVal = LlvmApi.BuildLoad2(target.Builder, i8, aPtr, "a_byte");
        LlvmValueHandle bVal = LlvmApi.BuildLoad2(target.Builder, i8, bPtr, "b_byte");
        LlvmValueHandle eq = LlvmApi.BuildICmp(target.Builder, LlvmIntPredicate.Eq, aVal, bVal, "bytes_eq");
        LlvmValueHandle nextIdx = LlvmApi.BuildAdd(target.Builder, idx, LlvmApi.ConstInt(i64, 1, 0), "next");
        LlvmApi.BuildStore(target.Builder, nextIdx, idxSlot);
        LlvmApi.BuildCondBr(target.Builder, eq, checkBlock, neBlock);

        // not_equal: return difference (a - b) as sign-extended i32
        LlvmApi.PositionBuilderAtEnd(target.Builder, neBlock);
        LlvmValueHandle aExt = LlvmApi.BuildZExt(target.Builder, aVal, i32, "a_ext");
        LlvmValueHandle bExt = LlvmApi.BuildZExt(target.Builder, bVal, i32, "b_ext");
        LlvmValueHandle diff = LlvmApi.BuildSub(target.Builder, aExt, bExt, "diff");
        LlvmApi.BuildRet(target.Builder, diff);

        // done: all bytes equal → return 0
        LlvmApi.PositionBuilderAtEnd(target.Builder, doneBlock);
        LlvmApi.BuildRet(target.Builder, LlvmApi.ConstInt(i32, 0, 0));
    }

    /// <summary>
    /// Emits a freestanding <c>bcmp(a, b, n)</c> implementation.
    /// LLVM may optimize <c>memcmp</c> equality checks into <c>bcmp</c> calls.
    /// Delegates to the builtin <c>memcmp</c>.
    /// </summary>
    private static void EmitBuiltinBcmp(
        LlvmTargetContext target, LlvmTypeHandle i8, LlvmTypeHandle i64, LlvmTypeHandle i8Ptr)
    {
        LlvmTypeHandle i32 = LlvmApi.Int32TypeInContext(target.Context);
        LlvmTypeHandle bcmpType = LlvmApi.FunctionType(i32, [i8Ptr, i8Ptr, i64]);
        LlvmValueHandle fn = LlvmApi.AddFunction(target.Module, "bcmp", bcmpType);
        ApplyBuiltinAttributes(target, fn, isReadOnly: true, pointerParamCount: 2);

        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(target.Context, fn, "entry");
        LlvmApi.PositionBuilderAtEnd(target.Builder, entry);

        LlvmValueHandle memcmpFn = LlvmApi.GetNamedFunction(target.Module, "memcmp");
        LlvmTypeHandle memcmpType = LlvmApi.FunctionType(i32, [i8Ptr, i8Ptr, i64]);
        LlvmValueHandle result = LlvmApi.BuildCall2(target.Builder, memcmpType, memcmpFn,
            [LlvmApi.GetParam(fn, 0), LlvmApi.GetParam(fn, 1), LlvmApi.GetParam(fn, 2)], "result");
        LlvmApi.BuildRet(target.Builder, result);
    }

    /// <summary>
    /// Applies standard LLVM function attributes to a freestanding builtin function.
    /// All builtins are nounwind (no exceptions) and willreturn (bounded loops).
    /// Read-only builtins (memcmp, bcmp, strlen) additionally get memory(read).
    /// Pointer parameters get noalias and nonnull where appropriate.
    /// Functions that return a pointer (memcpy, memset) get nonnull on the return value.
    /// </summary>
    private static void ApplyBuiltinAttributes(
        LlvmTargetContext target, LlvmValueHandle fn,
        bool isReadOnly,
        bool destSrcNoAlias = false,
        bool returnsPointer = false,
        uint pointerParamCount = 0)
    {
        // Function-level attributes
        uint nounwindKind = LlvmApi.GetEnumAttributeKindForName("nounwind");
        uint willreturnKind = LlvmApi.GetEnumAttributeKindForName("willreturn");

        LlvmApi.AddAttributeAtIndex(fn, LlvmApi.AttributeIndexFunction,
            LlvmApi.CreateEnumAttribute(target.Context, nounwindKind, 0));
        LlvmApi.AddAttributeAtIndex(fn, LlvmApi.AttributeIndexFunction,
            LlvmApi.CreateEnumAttribute(target.Context, willreturnKind, 0));

        // Read-only builtins get memory(read) — they never write to memory.
        // In LLVM 16+ this replaces the old 'readonly' function attribute.
        if (isReadOnly)
        {
            LlvmAttributeHandle memReadAttr = LlvmApi.CreateStringAttribute(target.Context, "memory", "read");
            LlvmApi.AddAttributeAtIndex(fn, LlvmApi.AttributeIndexFunction, memReadAttr);
        }

        // Pointer parameter attributes
        uint nonnullKind = LlvmApi.GetEnumAttributeKindForName("nonnull");
        uint noaliasKind = LlvmApi.GetEnumAttributeKindForName("noalias");
        uint readonlyKind = LlvmApi.GetEnumAttributeKindForName("readonly");

        for (uint i = 0; i < pointerParamCount; i++)
        {
            // All pointer parameters to builtins are non-null (we never pass null).
            uint paramIndex = i + 1; // LLVM param indices start at 1 (0 = return)
            LlvmApi.AddAttributeAtIndex(fn, paramIndex,
                LlvmApi.CreateEnumAttribute(target.Context, nonnullKind, 0));

            // memcpy/memset: dest and src don't alias (C standard requires non-overlapping).
            if (destSrcNoAlias)
            {
                LlvmApi.AddAttributeAtIndex(fn, paramIndex,
                    LlvmApi.CreateEnumAttribute(target.Context, noaliasKind, 0));
            }

            // Read-only functions: all pointer params are readonly (no writes through them).
            if (isReadOnly)
            {
                LlvmApi.AddAttributeAtIndex(fn, paramIndex,
                    LlvmApi.CreateEnumAttribute(target.Context, readonlyKind, 0));
            }
        }

        // Return value: nonnull for functions that return a pointer (memcpy, memset).
        if (returnsPointer)
        {
            LlvmApi.AddAttributeAtIndex(fn, LlvmApi.AttributeIndexReturn,
                LlvmApi.CreateEnumAttribute(target.Context, nonnullKind, 0));
        }
    }

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
            SyscallMunmap => Arm64SyscallMunmap,
            SyscallLseek => Arm64SyscallLseek,
            SyscallSocket => Arm64SyscallSocket,
            SyscallConnect => Arm64SyscallConnect,
            SyscallBind => Arm64SyscallBind,
            SyscallListen => Arm64SyscallListen,
            SyscallSetsockopt => Arm64SyscallSetsockopt,
            SyscallAccept4 => Arm64SyscallAccept4,
            SyscallFcntl => Arm64SyscallFcntl,
            SyscallEpollCtl => Arm64SyscallEpollCtl,
            SyscallEpollWait => Arm64SyscallEpollPwait,
            SyscallEpollCreate1 => Arm64SyscallEpollCreate1,
            SyscallNanosleep => Arm64SyscallNanosleep,
            SyscallClockGettime => Arm64SyscallClockGettime,
            SyscallExit => Arm64SyscallExit,
            SyscallGetpid => Arm64SyscallGetpid,
            SyscallGetppid => Arm64SyscallGetppid,
            SyscallFutex => Arm64SyscallFutex,
            SyscallDup2 => Arm64SyscallDup3,
            SyscallFork => Arm64SyscallClone,
            SyscallExecve => Arm64SyscallExecve,
            SyscallWaitpid => Arm64SyscallWait4,
            SyscallKill => Arm64SyscallKill,
            SyscallPipe2 => Arm64SyscallPipe2,
            SyscallSchedGetaffinity => Arm64SyscallSchedGetaffinity,
            SyscallPrctl => Arm64SyscallPrctl,
            SyscallRtSigaction => Arm64SyscallRtSigaction,
            SyscallIoctl => Arm64SyscallIoctl,
            SyscallPpoll => Arm64SyscallPpoll,
            _ => throw new ArgumentOutOfRangeException(nameof(x86Nr), $"No AArch64 mapping for x86-64 syscall {x86Nr}.")
        };
    }
}
