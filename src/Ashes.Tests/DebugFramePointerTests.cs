using System.Runtime.InteropServices;
using Ashes.Backend.Backends;
using Ashes.Backend.Llvm;
using Ashes.Backend.Llvm.Interop;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class DebugFramePointerTests
{
    [Test]
    [Arguments(BackendOptimizationLevel.O0, true)]
    [Arguments(BackendOptimizationLevel.O2, true)]
    [Arguments(BackendOptimizationLevel.O0, false)]
    [Arguments(BackendOptimizationLevel.O2, false)]
    public void Debug_functions_keep_native_frame_chains(BackendOptimizationLevel level, bool debug)
    {
        IrFunction entry = new("entry", [new IrInst.LoadConstInt(0, 42), new IrInst.Return(0)], 0, 1, false);
        IrFunction leaf = new("frame_probe", [new IrInst.LoadConstInt(0, 7), new IrInst.Return(0)], 2, 1, true);
        IrProgram program = new(entry, [leaf], [], false, false, false, false, true, false);
        BackendCompileOptions options = new(level, EmitDebugInfo: debug);
        using LlvmTargetContext target = LlvmTargetSetup.Create(TargetIds.LinuxX64, level);
        // A naked native helper must remain prologue-free (as Linux's signal restorer does).
        LlvmValueHandle naked = LlvmApi.AddFunction(target.Module, "naked_probe",
            LlvmApi.FunctionType(LlvmApi.VoidTypeInContext(target.Context), []));
        LlvmApi.AddAttributeAtIndex(naked, LlvmApi.AttributeIndexFunction,
            LlvmApi.CreateEnumAttribute(target.Context, LlvmApi.GetEnumAttributeKindForName("naked"), 0));
        LlvmApi.PositionBuilderAtEnd(target.Builder,
            LlvmApi.AppendBasicBlockInContext(target.Context, naked, "entry"));
        LlvmApi.BuildUnreachable(target.Builder);
        LlvmCodegen.EmitLinuxX64ProgramModule(target, program, options, usesTlsRuntime: false);

        // Keep the unused leaf available for inspection after O2's global dead-code pass.
        // Its body otherwise takes the same lifted-function emission path as compiler helpers.
        LlvmApi.SetLinkage(LlvmApi.GetNamedFunction(target.Module, "frame_probe"), LlvmLinkage.External);
        LlvmCodegen.RunLlvmOptimizationPasses(target, level);
        string module = ReadModule(target);
        FunctionAttributes(module, "naked_probe").ShouldNotContain("\"frame-pointer\"");
        foreach (string name in new[] { "entry", "frame_probe", "memcpy" })
        {
            FunctionAttributes(module, name).Contains("\"frame-pointer\"=\"all\"", StringComparison.Ordinal)
                .ShouldBe(debug, $"frame-pointer policy for {name} at {level}");
        }

        if (debug)
        {
            string assembly = EmitAssembly(target);
            AssertFramePrologue(assembly, "entry");
            AssertFramePrologue(assembly, "frame_probe");
            AssertFramePrologue(assembly, "memcpy");
        }
        // Release functions may legitimately need a frame for other LLVM reasons;
        // the assertion above checks only that Ashes did not force this attribute.
    }

    private static string ReadModule(LlvmTargetContext target)
    {
        nint text = LlvmApi.PrintModuleToString(target.Module);
        try
        {
            return Marshal.PtrToStringUTF8(text)!;
        }
        finally
        {
            LlvmApi.DisposeMessage(text);
        }
    }

    private static string FunctionAttributes(string module, string name)
    {
        string[] lines = module.Split('\n');
        string definition = lines.Single(line => line.StartsWith("define ", StringComparison.Ordinal)
            && line.Contains("@" + name + "(", StringComparison.Ordinal));
        int marker = definition.IndexOf('#');
        marker.ShouldBeGreaterThanOrEqualTo(0);
        string group = definition[marker..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return lines.Single(line => line.StartsWith("attributes " + group + " = ", StringComparison.Ordinal));
    }

    private static string EmitAssembly(LlvmTargetContext target)
    {
        int error = LlvmApi.TargetMachineEmitToMemoryBuffer(target.TargetMachine, target.Module,
            LlvmCodeGenFileType.Assembly, out nint message, out nint buffer);
        if (error != 0)
        {
            string diagnostic = Marshal.PtrToStringUTF8(message) ?? "LLVM assembly emission failed";
            LlvmApi.DisposeMessage(message);
            throw new InvalidOperationException(diagnostic);
        }

        try
        {
            return Marshal.PtrToStringUTF8(LlvmApi.GetBufferStart(buffer), checked((int)LlvmApi.GetBufferSize(buffer)));
        }
        finally
        {
            LlvmApi.DisposeMemoryBuffer(buffer);
        }
    }

    private static void AssertFramePrologue(string assembly, string function)
    {
        int start = assembly.IndexOf("\n" + function + ":", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        int end = assembly.IndexOf(".Lfunc_end", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        string instructions = string.Join(' ', assembly[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        instructions.ShouldContain("pushq %rbp");
        instructions.ShouldContain("movq %rsp, %rbp");
    }
}
