using System.Reflection;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class WindowsBackendCoverageTests
{
    [Test]
    public void Windows_backend_compile_should_emit_pe_header_for_int_program()
    {
        var bytes = CompileForWindows("Ashes.IO.print(40 + 2)");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
    }

    [Test]
    public void Windows_backend_compile_should_support_compiler_features_used_by_ashes_programs()
    {
        var bytes = CompileForWindows("let z = 20 in let f = fun (x) -> if x <= z then x + z else x + 1 in Ashes.IO.print(f(22))");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
    }

    [Test]
    public void Windows_backend_compile_should_support_string_concat_programs()
    {
        var bytes = CompileForWindows("Ashes.IO.print(\"hello \" + \"world\")");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
    }

    [Test]
    public void Windows_backend_compile_should_support_large_rdata_programs()
    {
        var bytes = CompileForWindows($"Ashes.IO.print(\"{new string('a', 20000)}\")");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
    }

    [Test]
    public void Windows_backend_compile_should_not_emit_a_constant_stub_for_simple_programs()
    {
        var first = CompileForWindows("40 + 2");
        var second = CompileForWindows("40 + 3");

        first.ShouldNotBe(second);
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_float_arithmetic_and_comparisons()
    {
        var ir = LowerExpression("if (1.5 + 2.5) == 4.0 then 42 else 0");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_heap_backed_tuple_and_list_programs()
    {
        var ir = LowerExpression("match ([1, 2], (3, 4)) with | (x :: _, (a, b)) -> x + a + b | _ -> 0");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_adt_field_programs()
    {
        var ir = LowerProgram("""
            type Pair = | Pair(A, B)
            let value = Pair(40, 2)
            in match value with
            | Pair(a, b) -> a + b
            """);

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_string_compare_and_concat_programs()
    {
        var ir = LowerExpression("if (\"he\" + \"llo\") == \"hello\" then 1 else 0");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_closure_programs()
    {
        var ir = LowerExpression("let z = 20 in let f = fun (x) -> x + z in f(22)");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    private static byte[] CompileForWindows(string source)
    {
        var ir = LowerExpression(source);
        return new WindowsX64PeBackend().Compile(ir);
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

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static bool SupportsMinimalLlvm(string methodName, IrProgram ir)
    {
        var method = typeof(WindowsX64PeBackend).Assembly
            .GetType("Ashes.Backend.Llvm.LlvmCodegen", throwOnError: true)!
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [ir])!;
    }
}
