using System.Reflection;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class LinuxBackendCoverageTests
{
    [Test]
    public void Linux_backend_compile_should_support_compiler_features_used_by_ashes_programs()
    {
        var bytes = CompileForLinux("let z = 20 in let f = fun (x) -> if x <= z then x + z else x + 1 in Ashes.IO.print(f(22))");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_compile_should_not_emit_a_constant_image_for_simple_programs()
    {
        var first = CompileForLinux("40 + 2");
        var second = CompileForLinux("40 + 3");

        first.ShouldNotBe(second);
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_float_arithmetic_and_comparisons()
    {
        var ir = LowerExpression("if (1.5 + 2.5) == 4.0 then Ashes.IO.print(42) else Ashes.IO.print(0)");

        SupportsMinimalLlvm("SupportsMinimalLinuxLlvm", ir).ShouldBeTrue();
    }

    private static byte[] CompileForLinux(string source)
    {
        var ir = LowerExpression(source);
        return new LinuxX64LlvmBackend().Compile(ir);
    }

    private static IrProgram LowerExpression(string source)
    {
        var diagnostics = new Diagnostics();
        var ast = new Parser(source, diagnostics).ParseExpression();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(ast);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static bool SupportsMinimalLlvm(string methodName, IrProgram ir)
    {
        var method = typeof(LinuxX64LlvmBackend).Assembly
            .GetType("Ashes.Backend.Llvm.LlvmCodegen", throwOnError: true)!
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [ir])!;
    }
}
