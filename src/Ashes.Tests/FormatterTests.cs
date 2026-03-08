using Ashes.Frontend;
using System.Runtime.CompilerServices;
using Shouldly;

namespace Ashes.Tests;

public sealed class FormatterTests
{
    public sealed record FormatterFixture(string Name, string InputPath, string ExpectedPath);

    private static string GetFormatterFixturesRoot([CallerFilePath] string? callerFile = null)
    {
        var sourceDir = Path.GetDirectoryName(callerFile)!;
        return Path.GetFullPath(Path.Combine(sourceDir, "..", "..", "tests", "formatter"));
    }

    public static IEnumerable<FormatterFixture> FormatterFixtures()
    {
        var fixturesRoot = GetFormatterFixturesRoot();
        if (!Directory.Exists(fixturesRoot))
        {
            yield break;
        }

        foreach (var inputPath in Directory.GetFiles(fixturesRoot, "*.input.txt").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var expectedPath = inputPath.Replace(".input.txt", ".expected.txt", StringComparison.Ordinal);
            File.Exists(expectedPath).ShouldBeTrue($"Missing golden file for {Path.GetFileName(inputPath)}");
            yield return new FormatterFixture(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(inputPath)), inputPath, expectedPath);
        }
    }

    private static string FormatFixtureSource(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.Errors.ShouldBeEmpty();

        return Ashes.Formatter.Formatter.Format(
            program,
            preferPipelines: source.Contains("|>", StringComparison.Ordinal)
                || source.Contains("|?>", StringComparison.Ordinal)
                || source.Contains("|!>", StringComparison.Ordinal));
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string TrimTrailingLineWhitespace(string text)
    {
        var lines = NormalizeLineEndings(text).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd(' ', '\t');
        }

        return string.Join("\n", lines);
    }

    private static string EnsureTrailingNewline(string text)
    {
        return text.EndsWith('\n') ? text : text + "\n";
    }

    [Test]
    public void Format_should_escape_string_literals()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.StrLit("a\\b\"c\nd\re\tf"));

        formatted.ShouldBe("\"a\\\\b\\\"c\\nd\\re\\tf\"\n");
    }

    [Test]
    public void Format_should_write_float_literals()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.FloatLit(3.5, "3.5"));

        formatted.ShouldBe("3.5\n");
    }

    [Test]
    public void Format_should_write_multiline_let_value()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Let(
                "x",
                new Expr.Let("y", new Expr.IntLit(1), new Expr.Var("y")),
                new Expr.Var("x")));

        formatted.ShouldBe("let x = \n    let y = 1\n    in y\nin x\n");
    }

    [Test]
    public void Format_should_write_multiline_let_body()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Let(
                "x",
                new Expr.IntLit(1),
                new Expr.Let("y", new Expr.IntLit(2), new Expr.Var("y"))));

        formatted.ShouldBe("let x = 1\nin \n    let y = 2\n    in y\n");
    }

    [Test]
    public void Format_should_write_multiline_let_result_body()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.LetResult(
                "x",
                new Expr.Call(new Expr.Var("parse"), new Expr.StrLit("42")),
                new Expr.LetResult("y", new Expr.Call(new Expr.Var("Ok"), new Expr.Var("x")), new Expr.Call(new Expr.Var("Ok"), new Expr.Var("y")))));

        formatted.ShouldBe("let? x = parse(\"42\")\nin \n    let? y = Ok(x)\n    in Ok(y)\n");
    }

    [Test]
    public void Format_should_write_multiline_if_branches()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.If(
                new Expr.BoolLit(true),
                new Expr.Let("x", new Expr.IntLit(1), new Expr.Var("x")),
                new Expr.Let("y", new Expr.IntLit(2), new Expr.Var("y"))));

        formatted.ShouldBe("if true\nthen \n    let x = 1\n    in x\nelse \n    let y = 2\n    in y\n");
    }

    [Test]
    public void Format_policy_should_write_let_in_layout()
    {
        const string source = "let x = let y = 1 in y in x\n";

        var formatted = FormatFixtureSource(source);

        formatted.ShouldBe("let x = \n    let y = 1\n    in y\nin x\n");
    }

    [Test]
    public void Format_policy_should_write_if_then_else_layout()
    {
        const string source = "if true then let x = 1 in x else let y = 2 in y\n";

        var formatted = FormatFixtureSource(source);

        formatted.ShouldBe("if true\nthen \n    let x = 1\n    in x\nelse \n    let y = 2\n    in y\n");
    }

    [Test]
    public void Format_policy_should_write_match_layout()
    {
        const string source = "match xs with | [] -> 0 | head :: tail -> match tail with | [] -> head | _ -> head\n";

        var formatted = FormatFixtureSource(source);

        formatted.ShouldBe("match xs with\n    | [] -> 0\n    | head :: tail -> \n        match tail with\n            | [] -> head\n            | _ -> head\n");
    }

    [Test]
    public void Format_should_write_singleline_if_branches()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.If(
                new Expr.BoolLit(true),
                new Expr.IntLit(1),
                new Expr.IntLit(2)));

        formatted.ShouldBe("if true\nthen 1\nelse 2\n");
    }

    [Test]
    public void Format_should_write_multiline_lambda_body()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Lambda(
                "x",
                new Expr.Let("y", new Expr.IntLit(1), new Expr.Var("y"))));

        formatted.ShouldBe("fun (x) -> \n    let y = 1\n    in y\n");
    }

    [Test]
    public void Format_should_parenthesize_complex_call_function()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Call(
                new Expr.Lambda("x", new Expr.Var("x")),
                new Expr.IntLit(1)));

        formatted.ShouldBe("((fun (x) -> x))(1)\n");
    }

    [Test]
    public void Format_should_format_comparison_operators()
    {
        var ge = Ashes.Formatter.Formatter.Format(
            new Expr.GreaterOrEqual(new Expr.IntLit(1), new Expr.IntLit(2)));
        var le = Ashes.Formatter.Formatter.Format(
            new Expr.LessOrEqual(new Expr.IntLit(3), new Expr.IntLit(4)));

        ge.ShouldBe("1 >= 2\n");
        le.ShouldBe("3 <= 4\n");
    }

    [Test]
    public void Format_should_parenthesize_comparison_in_cons_head()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Cons(
                new Expr.GreaterOrEqual(new Expr.IntLit(1), new Expr.IntLit(2)),
                new Expr.ListLit([])));

        formatted.ShouldBe("(1 >= 2) :: []\n");
    }

    [Test]
    public void Format_should_format_arithmetic_operators_with_precedence()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Subtract(
                new Expr.Add(
                    new Expr.IntLit(1),
                    new Expr.Multiply(new Expr.IntLit(2), new Expr.IntLit(3))),
                new Expr.Divide(new Expr.IntLit(4), new Expr.IntLit(2))));

        formatted.ShouldBe("1 + 2 * 3 - 4 / 2\n");
    }

    [Test]
    public void Format_should_write_call_chains_as_pipeline()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Call(
                new Expr.Var("print"),
                new Expr.Call(
                    new Expr.Var("double"),
                    new Expr.Call(new Expr.Var("inc"), new Expr.IntLit(1)))),
            preferPipelines: true);

        formatted.ShouldBe("1\n|> inc\n|> double\n|> print\n");
    }

    [Test]
    public void Format_should_preserve_subtraction_rhs_grouping()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Subtract(
                new Expr.IntLit(1),
                new Expr.Subtract(new Expr.IntLit(2), new Expr.IntLit(3))));

        formatted.ShouldBe("1 - (2 - 3)\n");
    }

    [Test]
    public void Format_should_write_unary_negation()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Subtract(
                new Expr.IntLit(0),
                new Expr.Add(new Expr.IntLit(1), new Expr.IntLit(2))));

        formatted.ShouldBe("-(1 + 2)\n");
    }

    [Test]
    public void Format_should_preserve_division_rhs_grouping()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Divide(
                new Expr.IntLit(8),
                new Expr.Divide(new Expr.IntLit(4), new Expr.IntLit(2))));

        formatted.ShouldBe("8 / (4 / 2)\n");
    }

    [Test]
    public void Format_should_support_multiline_expression_inside_print()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Call(
                new Expr.Var("print"),
                new Expr.Let("x", new Expr.IntLit(1), new Expr.Var("x"))));

        formatted.ShouldBe("print(let x = 1\nin x)\n");
    }

    [Test]
    public void Format_should_write_letrec()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.LetRec(
                "loop",
                new Expr.Lambda("i", new Expr.Var("i")),
                new Expr.Var("loop")));

        formatted.ShouldBe("let rec loop = \n    fun (i) -> i\nin loop\n");
    }

    [Test]
    public void Format_should_write_type_declaration_with_indent()
    {
        var program = new Program(
            new[]
            {
                new TypeDecl(
                    "Option",
                    [],
                    new[]
                    {
                        new TypeConstructor("None", []),
                        new TypeConstructor("Some", ["T"]),
                    })
            },
            new Expr.Call(new Expr.Var("print"), new Expr.StrLit("ok")));

        var formatted = Ashes.Formatter.Formatter.Format(program);

        formatted.ShouldBe("type Option =\n    | None\n    | Some(T)\n\nprint(\"ok\")\n");
    }

    [Test]
    public void Format_type_declaration_is_idempotent()
    {
        const string source = "type Option =\n    | None\n    | Some(T)\n\nprint(\"ok\")\n";
        var diag = new Ashes.Frontend.Diagnostics();
        var program = new Ashes.Frontend.Parser(source, diag).ParseProgram();
        var formatted = Ashes.Formatter.Formatter.Format(program);

        formatted.ShouldBe(source);
    }

    [Test]
    public void Format_should_write_multiline_match_with_four_space_indentation()
    {
        const string source = "type Result = | Ok(T) | Error(T)\nlet r1 = Ok(5) in let r2 = match r1 with | Ok(x) -> Ok(x + 1) | Error(e) -> Error(e) in match r2 with | Ok(_) -> print(\"ok\") | Error(_) -> print(\"error\")";
        var diag = new Ashes.Frontend.Diagnostics();
        var program = new Ashes.Frontend.Parser(source, diag).ParseProgram();
        var formatted = Ashes.Formatter.Formatter.Format(program);

        formatted.ShouldContain("type Result =\n    | Ok(T)\n    | Error(T)\n");
        formatted.ShouldContain("match r1 with\n            | Ok(x) -> Ok(x + 1)\n            | Error(e) -> Error(e)");
        formatted.ShouldContain("match r2 with\n            | Ok(_) -> print(\"ok\")\n            | Error(_) -> print(\"error\")");
    }

    [Test]
    public void Format_should_write_explicit_type_parameters_with_parentheses()
    {
        var program = new Ashes.Frontend.Program(
            [
                new TypeDecl(
                    "Result",
                    [new TypeParameter("E"), new TypeParameter("A")],
                    [
                        new TypeConstructor("Ok", ["A"]),
                        new TypeConstructor("Error", ["E"])
                    ])
            ],
            new Expr.Call(new Expr.Var("print"), new Expr.StrLit("ok")));

        var formatted = Ashes.Formatter.Formatter.Format(program);

        formatted.ShouldContain("type Result(E, A) =\n    | Ok(A)\n    | Error(E)\n");
    }

    [Test]
    public void Format_should_preserve_mixed_result_pipeline_layout()
    {
        const string source = "let x = Ok(\"42\") |?> parse |!> wrap in x\n";
        var diag = new Ashes.Frontend.Diagnostics();
        var expr = new Ashes.Frontend.Parser(source, diag).ParseExpression();
        diag.Errors.ShouldBeEmpty();

        var formatted = Ashes.Formatter.Formatter.Format(expr, preferPipelines: true);

        formatted.ShouldContain("Ok(\"42\")\n    |?> parse\n    |!> wrap");
    }

    [Test]
    public void Format_should_support_two_space_indentation_option()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Let(
                "x",
                new Expr.Let("y", new Expr.IntLit(1), new Expr.Var("y")),
                new Expr.Var("x")),
            options: new Ashes.Formatter.FormattingOptions { IndentSize = 2, UseTabs = false, NewLine = "\n" });

        formatted.ShouldBe("let x = \n  let y = 1\n  in y\nin x\n");
    }

    [Test]
    public void Format_should_support_tab_indentation_option()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Let(
                "x",
                new Expr.Let("y", new Expr.IntLit(1), new Expr.Var("y")),
                new Expr.Var("x")),
            options: new Ashes.Formatter.FormattingOptions { IndentSize = 4, UseTabs = true, NewLine = "\n" });

        formatted.ShouldBe("let x = \n\tlet y = 1\n\tin y\nin x\n");
    }

    [Test]
    public void Format_should_support_crlf_newlines()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Let("x", new Expr.IntLit(1), new Expr.Var("x")),
            options: new Ashes.Formatter.FormattingOptions { IndentSize = 4, UseTabs = false, NewLine = "\r\n" });

        formatted.ShouldBe("let x = 1\r\nin x\r\n");
    }

    [Test]
    public void Format_should_write_whitespace_application_with_space()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Call(
                new Expr.Var("f"),
                new Expr.IntLit(42))
            { IsWhitespaceApplication = true });

        formatted.ShouldBe("f 42\n");
    }

    [Test]
    public void Format_should_write_chained_whitespace_application()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Call(
                new Expr.Call(
                    new Expr.Var("f"),
                    new Expr.Var("x"))
                { IsWhitespaceApplication = true },
                new Expr.Var("y"))
            { IsWhitespaceApplication = true });

        formatted.ShouldBe("f x y\n");
    }

    [Test]
    public void Format_should_preserve_paren_call_style()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Call(
                new Expr.Var("f"),
                new Expr.IntLit(42)));

        formatted.ShouldBe("f(42)\n");
    }

    [Test]
    public void Format_whitespace_application_is_idempotent()
    {
        const string source = "print 42\n";
        var diag = new Ashes.Frontend.Diagnostics();
        var program = new Ashes.Frontend.Parser(source, diag).ParseExpression();
        var formatted = Ashes.Formatter.Formatter.Format(program);

        formatted.ShouldBe(source);
    }

    [Test]
    public void Format_let_sugar_is_idempotent()
    {
        const string source = "let add x y = x + y\nin Ashes.IO.print(add(1)(2))\n";
        var diag = new Ashes.Frontend.Diagnostics();
        var program = new Ashes.Frontend.Parser(source, diag).ParseExpression();
        var formatted = Ashes.Formatter.Formatter.Format(program);

        formatted.ShouldBe(source);
    }

    [Test]
    public void Format_rec_let_sugar_is_preserved()
    {
        const string source = "let rec loop i = \n    if i >= 10\n    then i\n    else loop(i + 1)\nin Ashes.IO.print(loop(0))\n";
        var diag = new Ashes.Frontend.Diagnostics();
        var program = new Ashes.Frontend.Parser(source, diag).ParseExpression();
        var formatted = Ashes.Formatter.Formatter.Format(program);

        formatted.ShouldBe(source);
    }

    [Test]
    public void Format_nested_lambdas_without_sugar_params_stay_explicit()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.Let(
                "add",
                new Expr.Lambda(
                    "x",
                    new Expr.Lambda("y", new Expr.Add(new Expr.Var("x"), new Expr.Var("y")))),
                new Expr.Var("add")));

        formatted.ShouldBe("let add = \n    fun (x) -> \n        fun (y) -> x + y\nin add\n");
    }

    [Test]
    public void Formatter_fixture_corpus_should_cover_issue_three_baseline()
    {
        var fixtures = FormatterFixtures().ToArray();

        fixtures.Length.ShouldBeGreaterThanOrEqualTo(10);
        fixtures.Any(fixture => fixture.Name.Contains("torture", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
    }

    [Test]
    [MethodDataSource(nameof(FormatterFixtures))]
    public void Formatter_fixtures_should_be_canonical_and_idempotent(FormatterFixture fixture)
    {
        var input = NormalizeLineEndings(File.ReadAllText(fixture.InputPath));
        var expected = EnsureTrailingNewline(TrimTrailingLineWhitespace(File.ReadAllText(fixture.ExpectedPath)));

        var formatted = EnsureTrailingNewline(TrimTrailingLineWhitespace(FormatFixtureSource(input)));
        formatted.ShouldBe(expected, customMessage: fixture.Name);

        var secondPass = EnsureTrailingNewline(TrimTrailingLineWhitespace(FormatFixtureSource(formatted)));
        secondPass.ShouldBe(expected, customMessage: fixture.Name + " second pass");
    }
}
