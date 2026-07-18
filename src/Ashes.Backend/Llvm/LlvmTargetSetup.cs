using Ashes.Backend.Backends;
using System.Runtime.InteropServices;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static class LlvmTargetSetup
{
    private static bool _initialized;
    private static readonly Lock SyncRoot = new();

    public static LlvmTargetContext Create(string targetId, BackendOptimizationLevel optimizationLevel, string? targetCpu = null, long? parallelWorkerStackBytes = null, long? parallelWorkerCap = null)
    {
        EnsureInitialized();

        string targetTriple = ResolveTargetTriple(targetId);
        LlvmTargetHandle target = ResolveTarget(targetTriple);
        LlvmCodeGenOptLevel optLevel = ResolveOptLevel(optimizationLevel);
        (string cpu, string features) = ResolveCpuAndFeatures(targetId, targetCpu);

        LlvmTargetMachineHandle machine = LlvmApi.CreateTargetMachine(target,
            targetTriple,
            cpu,
            features,
            optLevel,
            LlvmRelocMode.Static,
            LlvmCodeModel.Default);

        LlvmContextHandle context = LlvmApi.ContextCreate();
        LlvmModuleHandle module = LlvmApi.ModuleCreateWithNameInContext($"ashes.{targetId}.module", context);
        LlvmApi.SetTarget(module, targetTriple);
        ApplyDataLayout(module, machine);

        LlvmBuilderHandle builder = LlvmApi.CreateBuilderInContext(context);
        return new LlvmTargetContext(context, module, builder, machine, targetTriple, parallelWorkerStackBytes, parallelWorkerCap);
    }

    private static string ResolveTargetTriple(string targetId)
    {
        return targetId switch
        {
            Backends.TargetIds.LinuxX64 => "x86_64-unknown-linux-gnu",
            Backends.TargetIds.LinuxArm64 => "aarch64-unknown-linux-gnu",
            Backends.TargetIds.WindowsX64 => "x86_64-pc-windows-msvc",
            Backends.TargetIds.WindowsArm64 => "aarch64-pc-windows-msvc",
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unknown target '{targetId}'."),
        };
    }

    private static LlvmTargetHandle ResolveTarget(string targetTriple)
    {
        int targetErr = LlvmApi.GetTargetFromTriple(targetTriple, out LlvmTargetHandle target, out nint targetErrMsg);
        if (targetErr != 0)
        {
            string errorMessage = Marshal.PtrToStringAnsi(targetErrMsg) ?? "unknown error";
            LlvmApi.DisposeMessage(targetErrMsg);
            throw new InvalidOperationException($"LLVM target lookup failed for '{targetTriple}': {errorMessage}");
        }

        return target;
    }

    private static LlvmCodeGenOptLevel ResolveOptLevel(BackendOptimizationLevel optimizationLevel)
    {
        return optimizationLevel switch
        {
            BackendOptimizationLevel.O0 => LlvmCodeGenOptLevel.None,
            BackendOptimizationLevel.O1 => LlvmCodeGenOptLevel.Less,
            BackendOptimizationLevel.O2 => LlvmCodeGenOptLevel.Default,
            BackendOptimizationLevel.O3 => LlvmCodeGenOptLevel.Aggressive,
            _ => throw new ArgumentOutOfRangeException(nameof(optimizationLevel)),
        };
    }

    // Resolve CPU name and features. When --target-cpu is not specified,
    // use safe generic defaults (runs on any CPU of the target arch).
    // When "native" is specified, LLVM detects the host CPU at compile time.
    private static (string Cpu, string Features) ResolveCpuAndFeatures(string targetId, string? targetCpu)
    {
        if (targetCpu is not null && targetCpu.Equals("native", StringComparison.OrdinalIgnoreCase))
        {
            return (LlvmApi.GetHostCPUName(), LlvmApi.GetHostCPUFeatures());
        }

        if (targetCpu is not null)
        {
            return (targetCpu, string.Empty);
        }

        bool isArm64 = string.Equals(targetId, Backends.TargetIds.LinuxArm64, StringComparison.Ordinal)
            || string.Equals(targetId, Backends.TargetIds.WindowsArm64, StringComparison.Ordinal);
        string cpu = isArm64 ? "generic" : "x86-64";
        return (cpu, string.Empty);
    }

    private static void ApplyDataLayout(LlvmModuleHandle module, LlvmTargetMachineHandle machine)
    {
        LlvmTargetDataHandle dataLayout = LlvmApi.CreateTargetDataLayout(machine);
        try
        {
            nint layoutText = LlvmApi.CopyStringRepOfTargetData(dataLayout);
            try
            {
                LlvmApi.SetDataLayout(module, Marshal.PtrToStringAnsi(layoutText)
                    ?? throw new InvalidOperationException("LLVM returned an empty target data layout."));
            }
            finally
            {
                LlvmApi.DisposeMessage(layoutText);
            }
        }
        finally
        {
            LlvmApi.DisposeTargetData(dataLayout);
        }
    }

    private static void EnsureInitialized()
    {
        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            LlvmApi.InitializeX86TargetInfo();
            LlvmApi.InitializeX86Target();
            LlvmApi.InitializeX86TargetMC();
            LlvmApi.InitializeX86AsmParser();
            LlvmApi.InitializeX86AsmPrinter();
            LlvmApi.InitializeAArch64TargetInfo();
            LlvmApi.InitializeAArch64Target();
            LlvmApi.InitializeAArch64TargetMC();
            LlvmApi.InitializeAArch64AsmParser();
            LlvmApi.InitializeAArch64AsmPrinter();
            _initialized = true;
        }
    }
}

internal sealed record LlvmTargetContext(
    LlvmContextHandle Context,
    LlvmModuleHandle Module,
    LlvmBuilderHandle Builder,
    LlvmTargetMachineHandle TargetMachine,
    string TargetTriple,
    // Per-worker stack size (bytes) for structured parallelism. Null = the built-in default
    // (see LlvmCodegen.DefaultParallelWorkerStackBytes on linux; the OS default on win-x64).
    long? ParallelWorkerStackBytes = null,
    // Max concurrent parallel workers. Null = detect the machine's core count at program start
    // (sched_getaffinity popcount on linux; GetSystemInfo on win-x64).
    long? ParallelWorkerCap = null) : IDisposable
{
    private int _moduleConstantCounter;

    private readonly Dictionary<string, LlvmValueHandle> _stringLiteralGlobals = new(StringComparer.Ordinal);

    private readonly Dictionary<string, LlvmValueHandle> _namedGlobals = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns a module-level global identified by <paramref name="key"/>, creating it via
    /// <paramref name="create"/> on first request and reusing it thereafter. Used for shared
    /// per-call scratch buffers (e.g. the <c>readLine</c> line buffer) that must NOT be
    /// stack-allocated: a fresh <c>alloca</c> per call leaks the stack when the call sits inside
    /// a TCO loop (one stack frame that jumps backward instead of returning), so the scratch is
    /// a single reused global instead. Safe because Ashes is single-threaded and these helpers
    /// are non-reentrant — the buffer is fully consumed (copied to the heap) before the call
    /// returns.
    /// </summary>
    public LlvmValueHandle GetOrAddNamedGlobal(string key, Func<LlvmValueHandle> create)
    {
        if (_namedGlobals.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var global = create();
        _namedGlobals[key] = global;
        return global;
    }

    /// <summary>Returns a module-unique integer for naming global constants.</summary>
    public int NextGlobalConstantId() =>
        System.Threading.Interlocked.Increment(ref _moduleConstantCounter);

    /// <summary>
    /// Content-addressed interning of string-literal globals. Returns the module-level
    /// constant global for <paramref name="value"/>, creating it via <paramref name="create"/>
    /// on first request and reusing it for every subsequent identical value. Compile-time only:
    /// the literal set is finite and static, so this is leak-free by construction. Identical
    /// literals — whether from user source or internal codegen call sites — share one
    /// <c>.rodata</c> global instead of emitting a duplicate per use.
    /// </summary>
    public LlvmValueHandle GetOrAddStringLiteralGlobal(string value, Func<LlvmValueHandle> create)
    {
        if (_stringLiteralGlobals.TryGetValue(value, out var existing))
        {
            return existing;
        }

        var global = create();
        _stringLiteralGlobals[value] = global;
        return global;
    }

    public void Dispose()
    {
        LlvmApi.DisposeBuilder(Builder);
        LlvmApi.DisposeModule(Module);
        LlvmApi.ContextDispose(Context);
        LlvmApi.DisposeTargetMachine(TargetMachine);
    }
}
