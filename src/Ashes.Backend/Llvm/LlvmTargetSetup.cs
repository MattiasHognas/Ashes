using Ashes.Backend.Backends;
using LLVMSharp.Interop;

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

        if (!LLVMTargetRef.TryGetTargetFromTriple(targetTriple, out LLVMTargetRef target, out string errorMessage))
        {
            throw new InvalidOperationException($"LLVM target lookup failed for '{targetTriple}': {errorMessage}");
        }

        LLVMCodeGenOptLevel optLevel = optimizationLevel switch
        {
            BackendOptimizationLevel.O0 => LLVMCodeGenOptLevel.LLVMCodeGenLevelNone,
            BackendOptimizationLevel.O1 => LLVMCodeGenOptLevel.LLVMCodeGenLevelLess,
            BackendOptimizationLevel.O2 => LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault,
            BackendOptimizationLevel.O3 => LLVMCodeGenOptLevel.LLVMCodeGenLevelAggressive,
            _ => throw new ArgumentOutOfRangeException(nameof(optimizationLevel)),
        };

        LLVMTargetMachineRef machine = target.CreateTargetMachine(
            targetTriple,
            "x86-64",
            string.Empty,
            optLevel,
            LLVMRelocMode.LLVMRelocStatic,
            LLVMCodeModel.LLVMCodeModelLarge);

        LLVMContextRef context = LLVMContextRef.Create();
        LLVMModuleRef module = context.CreateModuleWithName($"ashes.{targetId}.module");
        module.Target = targetTriple;
        module.DataLayout = machine.CreateTargetDataLayout().ToString();

        LLVMBuilderRef builder = context.CreateBuilder();
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

            LLVM.InitializeX86TargetInfo();
            LLVM.InitializeX86Target();
            LLVM.InitializeX86TargetMC();
            LLVM.InitializeX86AsmParser();
            LLVM.InitializeX86AsmPrinter();
            _initialized = true;
        }
    }
}

internal sealed record LlvmTargetContext(
    LLVMContextRef Context,
    LLVMModuleRef Module,
    LLVMBuilderRef Builder,
    LLVMTargetMachineRef TargetMachine,
    string TargetTriple) : IDisposable
{
    public void Dispose()
    {
        Builder.Dispose();
        Module.Dispose();
        Context.Dispose();
    }
}
