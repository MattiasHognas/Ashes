using System.Runtime.InteropServices;
using Ashes.Backend.Backends;
using Ashes.Backend.Llvm;
using Ashes.Backend.Llvm.Interop;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class LlvmIntegrationTests
{
    [Test]
    public void Invalid_new_pass_manager_pipeline_fails_with_llvm_diagnostic()
    {
        using LlvmTargetContext target = LlvmTargetSetup.Create(TargetIds.LinuxX64, BackendOptimizationLevel.O0);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            LlvmCodegen.RunLlvmPassPipeline(target, "definitely-not-an-llvm-pass"));

        exception.Message.ShouldContain("definitely-not-an-llvm-pass");
        exception.Message.ShouldContain("unknown pass name");
    }

    [Test]
    [Arguments(BackendOptimizationLevel.O1)]
    [Arguments(BackendOptimizationLevel.O2)]
    [Arguments(BackendOptimizationLevel.O3)]
    public void Valid_new_pass_manager_pipelines_succeed(BackendOptimizationLevel level)
    {
        using LlvmTargetContext target = LlvmTargetSetup.Create(TargetIds.LinuxX64, level);

        LlvmCodegen.RunLlvmOptimizationPasses(target, level);
    }

    [Test]
    [Arguments(Architecture.X64, TargetIds.LinuxX64)]
    [Arguments(Architecture.Arm64, TargetIds.LinuxArm64)]
    public void Native_cpu_is_allowed_for_matching_architectures(
        Architecture host,
        string targetId)
    {
        LlvmTargetArchitecture target = LlvmTargetSetup.ResolveTargetArchitecture(targetId);
        LlvmCpuSelection selection = LlvmTargetSetup.ResolveCpuSelection(host, target, "native");

        selection.UseHostDetection.ShouldBeTrue();
    }

    [Test]
    [Arguments(Architecture.X64, TargetIds.LinuxArm64, "x86-64", "aarch64")]
    [Arguments(Architecture.Arm64, TargetIds.LinuxX64, "aarch64", "x86-64")]
    public void Native_cpu_is_rejected_for_cross_compilation(
        Architecture host,
        string targetId,
        string expectedHost,
        string expectedTarget)
    {
        LlvmTargetArchitecture target = LlvmTargetSetup.ResolveTargetArchitecture(targetId);
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            LlvmTargetSetup.ResolveCpuSelection(host, target, "native"));

        exception.Message.ShouldContain($"from {expectedHost} to {expectedTarget}");
        exception.Message.ShouldContain("Specify an explicit target CPU or omit --target-cpu.");
    }

    [Test]
    [Arguments(Architecture.X64, TargetIds.LinuxArm64, "cortex-a72")]
    [Arguments(Architecture.Arm64, TargetIds.LinuxX64, "skylake")]
    public void Explicit_cross_architecture_cpu_is_preserved(
        Architecture host,
        string targetId,
        string cpu)
    {
        LlvmTargetArchitecture target = LlvmTargetSetup.ResolveTargetArchitecture(targetId);
        LlvmCpuSelection selection = LlvmTargetSetup.ResolveCpuSelection(host, target, cpu);

        selection.UseHostDetection.ShouldBeFalse();
        selection.Cpu.ShouldBe(cpu);
    }

    [Test]
    [Arguments(TargetIds.LinuxX64, BackendOptimizationLevel.O0)]
    [Arguments(TargetIds.LinuxX64, BackendOptimizationLevel.O1)]
    [Arguments(TargetIds.LinuxX64, BackendOptimizationLevel.O2)]
    [Arguments(TargetIds.LinuxX64, BackendOptimizationLevel.O3)]
    [Arguments(TargetIds.LinuxArm64, BackendOptimizationLevel.O0)]
    [Arguments(TargetIds.LinuxArm64, BackendOptimizationLevel.O1)]
    [Arguments(TargetIds.LinuxArm64, BackendOptimizationLevel.O2)]
    [Arguments(TargetIds.LinuxArm64, BackendOptimizationLevel.O3)]
    [Arguments(TargetIds.WindowsX64, BackendOptimizationLevel.O0)]
    [Arguments(TargetIds.WindowsX64, BackendOptimizationLevel.O1)]
    [Arguments(TargetIds.WindowsX64, BackendOptimizationLevel.O2)]
    [Arguments(TargetIds.WindowsX64, BackendOptimizationLevel.O3)]
    [Arguments(TargetIds.WindowsArm64, BackendOptimizationLevel.O0)]
    [Arguments(TargetIds.WindowsArm64, BackendOptimizationLevel.O1)]
    [Arguments(TargetIds.WindowsArm64, BackendOptimizationLevel.O2)]
    [Arguments(TargetIds.WindowsArm64, BackendOptimizationLevel.O3)]
    public void Every_target_compiles_at_every_optimization_level(
        string targetId,
        BackendOptimizationLevel level)
    {
        IrProgram program = LowerExpression("Ashes.IO.print(40 + 2)");

        byte[] executable = BackendFactory.Create(targetId).Compile(program, new BackendCompileOptions(level));

        executable.Length.ShouldBeGreaterThan(256);
    }

    private static IrProgram LowerExpression(string source)
    {
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program ast = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        IrProgram program = new Lowering(diagnostics).Lower(ast);
        diagnostics.ThrowIfAny();
        return program;
    }
}
