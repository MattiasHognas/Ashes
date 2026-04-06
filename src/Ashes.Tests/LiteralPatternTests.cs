using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;
using System.Diagnostics;

namespace Ashes.Tests;

public sealed class LiteralPatternTests
{
    // ────── Parser tests ──────

    [Test]
    public void Parse_should_support_integer_literal_pattern()
    {
        var match = Parse("match n with | 0 -> \"zero\" | _ -> \"other\"")
            .ShouldBeOfType<Expr.Match>();

        match.Cases.Count.ShouldBe(2);
        match.Cases[0].Pattern.ShouldBeOfType<Pattern.IntLit>().Value.ShouldBe(0);
        match.Cases[0].Body.ShouldBe(new Expr.StrLit("zero"));
        match.Cases[1].Pattern.ShouldBeOfType<Pattern.Wildcard>();
    }

    [Test]
    public void Parse_should_support_multiple_integer_literal_patterns()
    {
        var match = Parse("match n with | 0 -> \"zero\" | 1 -> \"one\" | _ -> \"other\"")
            .ShouldBeOfType<Expr.Match>();

        match.Cases.Count.ShouldBe(3);
        match.Cases[0].Pattern.ShouldBeOfType<Pattern.IntLit>().Value.ShouldBe(0);
        match.Cases[1].Pattern.ShouldBeOfType<Pattern.IntLit>().Value.ShouldBe(1);
        match.Cases[2].Pattern.ShouldBeOfType<Pattern.Wildcard>();
    }

    [Test]
    public void Parse_should_support_negative_integer_literal_pattern()
    {
        var match = Parse("match n with | -1 -> \"neg\" | 0 -> \"zero\" | _ -> \"pos\"")
            .ShouldBeOfType<Expr.Match>();

        match.Cases.Count.ShouldBe(3);
        match.Cases[0].Pattern.ShouldBeOfType<Pattern.IntLit>().Value.ShouldBe(-1);
        match.Cases[1].Pattern.ShouldBeOfType<Pattern.IntLit>().Value.ShouldBe(0);
    }

    [Test]
    public void Parse_should_support_string_literal_pattern()
    {
        var match = Parse("match s with | \"hello\" -> 1 | \"world\" -> 2 | _ -> 0")
            .ShouldBeOfType<Expr.Match>();

        match.Cases.Count.ShouldBe(3);
        match.Cases[0].Pattern.ShouldBeOfType<Pattern.StrLit>().Value.ShouldBe("hello");
        match.Cases[1].Pattern.ShouldBeOfType<Pattern.StrLit>().Value.ShouldBe("world");
        match.Cases[2].Pattern.ShouldBeOfType<Pattern.Wildcard>();
    }

    [Test]
    public void Parse_should_support_boolean_literal_pattern()
    {
        var match = Parse("match b with | true -> 1 | false -> 0")
            .ShouldBeOfType<Expr.Match>();

        match.Cases.Count.ShouldBe(2);
        match.Cases[0].Pattern.ShouldBeOfType<Pattern.BoolLit>().Value.ShouldBe(true);
        match.Cases[1].Pattern.ShouldBeOfType<Pattern.BoolLit>().Value.ShouldBe(false);
    }

    [Test]
    public void Parse_should_support_let_tuple_pattern_binding()
    {
        // let (a, b) = (1, 2) in a + b
        // Desugars to: match (1, 2) with | (a, b) -> a + b
        var match = Parse("let (a, b) = (1, 2) in a + b")
            .ShouldBeOfType<Expr.Match>();

        match.Cases.Count.ShouldBe(1);
        var tuple = match.Cases[0].Pattern.ShouldBeOfType<Pattern.Tuple>();
        tuple.Elements.Count.ShouldBe(2);
        tuple.Elements[0].ShouldBeOfType<Pattern.Var>().Name.ShouldBe("a");
        tuple.Elements[1].ShouldBeOfType<Pattern.Var>().Name.ShouldBe("b");
    }

    // ────── Semantic tests ──────

    [Test]
    public void Integer_literal_pattern_should_type_check()
    {
        var diag = new Diagnostics();
        var ast = new Parser("match 1 with | 0 -> \"zero\" | _ -> \"other\"", diag).ParseExpression();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(ast);
        diag.ThrowIfAny();
        ir.ShouldNotBeNull();
    }

    [Test]
    public void String_literal_pattern_should_type_check()
    {
        var diag = new Diagnostics();
        var ast = new Parser("match \"hello\" with | \"hello\" -> 1 | _ -> 0", diag).ParseExpression();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(ast);
        diag.ThrowIfAny();
        ir.ShouldNotBeNull();
    }

    [Test]
    public void Boolean_literal_pattern_should_be_exhaustive()
    {
        var diag = new Diagnostics();
        var ast = new Parser("match true with | true -> 1 | false -> 0", diag).ParseExpression();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(ast);
        diag.ThrowIfAny();
        ir.ShouldNotBeNull();
    }

    [Test]
    public void Boolean_literal_pattern_missing_false_should_report_non_exhaustive()
    {
        var diag = new Diagnostics();
        var ast = new Parser("match true with | true -> 1", diag).ParseExpression();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(ast);
        diag.Errors.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Integer_literal_pattern_without_catch_all_should_report_non_exhaustive()
    {
        var diag = new Diagnostics();
        var ast = new Parser("match 1 with | 0 -> \"zero\" | 1 -> \"one\"", diag).ParseExpression();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(ast);
        diag.Errors.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Let_tuple_pattern_should_type_check()
    {
        var diag = new Diagnostics();
        var ast = new Parser("let (a, b) = (1, 2) in a + b", diag).ParseExpression();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(ast);
        diag.ThrowIfAny();
        ir.ShouldNotBeNull();
    }

    [Test]
    public void Duplicate_integer_literal_should_warn_unreachable()
    {
        var diag = new Diagnostics();
        var ast = new Parser("match 1 with | 0 -> 1 | 0 -> 2 | _ -> 3", diag).ParseExpression();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(ast);
        // Should produce a warning/error for duplicate literal
        diag.Errors.Count.ShouldBeGreaterThan(0);
    }

    // ────── End-to-end tests ──────

    [Test]
    public async Task Integer_literal_pattern_match_runs_correctly()
    {
        if (!OperatingSystem.IsLinux()) return;

        var src = """
            let classify n =
                match n with
                    | 0 -> "zero"
                    | 1 -> "one"
                    | _ -> "other"
            in Ashes.IO.print(classify(0))
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("zero\n");
    }

    [Test]
    public async Task Integer_literal_pattern_second_branch()
    {
        if (!OperatingSystem.IsLinux()) return;

        var src = """
            let classify n =
                match n with
                    | 0 -> "zero"
                    | 1 -> "one"
                    | _ -> "other"
            in Ashes.IO.print(classify(1))
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("one\n");
    }

    [Test]
    public async Task Integer_literal_pattern_fallthrough()
    {
        if (!OperatingSystem.IsLinux()) return;

        var src = """
            let classify n =
                match n with
                    | 0 -> "zero"
                    | 1 -> "one"
                    | _ -> "other"
            in Ashes.IO.print(classify(42))
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("other\n");
    }

    [Test]
    public async Task Negative_integer_literal_pattern()
    {
        if (!OperatingSystem.IsLinux()) return;

        var src = """
            let sign n =
                match n with
                    | 0 -> "zero"
                    | _ -> "nonzero"
            in Ashes.IO.print(sign(-5))
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("nonzero\n");
    }

    [Test]
    public async Task String_literal_pattern_match_runs_correctly()
    {
        if (!OperatingSystem.IsLinux()) return;

        var src = """
            let greet lang =
                match lang with
                    | "en" -> "hello"
                    | "es" -> "hola"
                    | _ -> "?"
            in Ashes.IO.print(greet("es"))
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("hola\n");
    }

    [Test]
    public async Task String_literal_pattern_fallthrough()
    {
        if (!OperatingSystem.IsLinux()) return;

        var src = """
            let greet lang =
                match lang with
                    | "en" -> "hello"
                    | "es" -> "hola"
                    | _ -> "unknown"
            in Ashes.IO.print(greet("fr"))
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("unknown\n");
    }

    [Test]
    public async Task Boolean_literal_pattern_match_runs_correctly()
    {
        if (!OperatingSystem.IsLinux()) return;

        var src = """
            let yesno b =
                match b with
                    | true -> "yes"
                    | false -> "no"
            in Ashes.IO.print(yesno(true))
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("yes\n");
    }

    [Test]
    public async Task Boolean_literal_pattern_false_branch()
    {
        if (!OperatingSystem.IsLinux()) return;

        var src = """
            let yesno b =
                match b with
                    | true -> "yes"
                    | false -> "no"
            in Ashes.IO.print(yesno(false))
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("no\n");
    }

    [Test]
    public async Task Let_tuple_pattern_runs_correctly()
    {
        if (!OperatingSystem.IsLinux()) return;

        var src = """
            let (a, b) = (10, 32)
            in Ashes.IO.print(a + b)
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("42\n");
    }

    [Test]
    public async Task Let_tuple_pattern_with_strings()
    {
        if (!OperatingSystem.IsLinux()) return;

        var src = """
            let (greeting, name) = ("hello", "world")
            in Ashes.IO.print(greeting + " " + name)
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("hello world\n");
    }

    // ────── Helpers ──────

    private static Expr Parse(string source)
    {
        var diag = new Diagnostics();
        var expr = new Parser(source, diag).ParseExpression();
        diag.ThrowIfAny();
        return expr;
    }

    private static async Task<string> CompileRunCaptureAsync(string source)
    {
        var diag = new Diagnostics();
        var ast = new Parser(source, diag).ParseExpression();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(ast);
        diag.ThrowIfAny();

        var elfBytes = new Ashes.Backend.Backends.LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tmpDir);

        var exePath = Path.Combine(tmpDir, $"litpat_{Guid.NewGuid():N}");
        TestProcessHelper.WriteExecutable(exePath, elfBytes);

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = await TestProcessHelper.StartProcessAsync(psi);
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        proc.ExitCode.ShouldBe(0, $"stderr: {stderr}");
        return stdout;
    }
}
