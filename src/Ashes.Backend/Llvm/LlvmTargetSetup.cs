using Ashes.Backend.Backends;
using System.Runtime.InteropServices;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static class LlvmTargetSetup
{
    private static bool _initialized;
    private static readonly Lock SyncRoot = new();

    public static LlvmTargetContext Create(string targetId, BackendOptimizationLevel optimizationLevel)
    {
        EnsureInitialized();

        string targetTriple = targetId switch
        {
            Backends.TargetIds.LinuxX64 => "x86_64-unknown-linux-gnu",
            Backends.TargetIds.WindowsX64 => "x86_64-pc-windows-msvc",
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unknown target '{targetId}'."),
        };

        int targetErr = LlvmApi.GetTargetFromTriple(targetTriple, out LlvmTargetHandle target, out nint targetErrMsg);
        if (targetErr != 0)
        {
            string errorMessage = Marshal.PtrToStringAnsi(targetErrMsg) ?? "unknown error";
            LlvmApi.DisposeMessage(targetErrMsg);
            throw new InvalidOperationException($"LLVM target lookup failed for '{targetTriple}': {errorMessage}");
        }

        LlvmCodeGenOptLevel optLevel = optimizationLevel switch
        {
            BackendOptimizationLevel.O0 => LlvmCodeGenOptLevel.None,
            BackendOptimizationLevel.O1 => LlvmCodeGenOptLevel.Less,
            BackendOptimizationLevel.O2 => LlvmCodeGenOptLevel.Default,
            BackendOptimizationLevel.O3 => LlvmCodeGenOptLevel.Aggressive,
            _ => throw new ArgumentOutOfRangeException(nameof(optimizationLevel)),
        };

        LlvmTargetMachineHandle machine = LlvmApi.CreateTargetMachine(target,
            targetTriple,
            "x86-64",
            string.Empty,
            optLevel,
            LlvmRelocMode.Static,
            LlvmCodeModel.Default);

        LlvmContextHandle context = LlvmApi.ContextCreate();
        LlvmModuleHandle module = LlvmApi.ModuleCreateWithNameInContext($"ashes.{targetId}.module", context);
        LlvmApi.SetTarget(module, targetTriple);
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

        LlvmBuilderHandle builder = LlvmApi.CreateBuilderInContext(context);
        return new LlvmTargetContext(context, module, builder, machine, targetTriple);
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
            _initialized = true;
        }
    }
}

internal sealed record LlvmTargetContext(
    LlvmContextHandle Context,
    LlvmModuleHandle Module,
    LlvmBuilderHandle Builder,
    LlvmTargetMachineHandle TargetMachine,
    string TargetTriple) : IDisposable
{
    public void Dispose()
    {
        LlvmApi.DisposeBuilder(Builder);
        LlvmApi.DisposeModule(Module);
        LlvmApi.ContextDispose(Context);
        LlvmApi.DisposeTargetMachine(TargetMachine);
    }
}
