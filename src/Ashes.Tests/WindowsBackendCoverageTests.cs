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

    private static byte[] CompileForWindows(string source)
    {
        var diagnostics = new Diagnostics();
        var ast = new Parser(source, diagnostics).ParseExpression();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(ast);
        diagnostics.ThrowIfAny();

        return new WindowsX64PeBackend().Compile(ir);
    }
}
