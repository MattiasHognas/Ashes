using Ashes.Frontend;
using Ashes.Lsp;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Targeted tests to cover previously untested code paths in DocumentService:
///  – .Span property accessors on DiagnosticItem / HoverItem / DefinitionItem
///  – GetCompletions / GetDefinition inside binary expressions
///  – ResolveDefinitionInExpr branches: binary, LetResult, LetRec, If, Lambda, Call, Cons
///  – ResolveDefinitionInPattern branches: Cons, Tuple
///  – ValidateStandaloneImports unknown Ashes module path
///  – GetHover / GetDefinition null return paths
///  – Nested let binding resolution in imported module (TryFindBindingDefinition recursion)
/// </summary>
public sealed class LspDocumentServiceCoverageTests
{
    // ── .Span property accessors ────────────────────────────────────────

    [Test]
    public void DiagnosticItem_Span_should_equal_TextSpan_from_Start_and_End()
    {
        var diagnostics = DocumentService.Analyze("if true then 1");

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].Span.Start.ShouldBe(diagnostics[0].Start);
        diagnostics[0].Span.End.ShouldBe(diagnostics[0].End);
    }

    [Test]
    public void HoverItem_Span_should_equal_TextSpan_from_Start_and_End()
    {
        const string source = "let id = fun (x) -> x in id(1)";

        var hover = DocumentService.GetHover(source, source.IndexOf("id", StringComparison.Ordinal));

        hover.ShouldNotBeNull();
        hover.Value.Span.Start.ShouldBe(hover.Value.Start);
        hover.Value.Span.End.ShouldBe(hover.Value.End);
    }

    [Test]
    public void DefinitionItem_Span_should_equal_TextSpan_from_Start_and_End()
    {
        var root = CreateTempDir();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "let x = 1 in x";
            File.WriteAllText(mainPath, source);

            var definition = DocumentService.GetDefinition(source, source.LastIndexOf('x'), mainPath);

            definition.ShouldNotBeNull();
            definition.Value.Span.Start.ShouldBe(definition.Value.Start);
            definition.Value.Span.End.ShouldBe(definition.Value.End);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── GetCompletions inside binary expressions ────────────────────────

    [Test]
    public void GetCompletions_at_position_inside_add_expression_should_include_bound_variable()
    {
        const string source = "let x = 1 in x + 2";
        var pos = source.LastIndexOf('x');

        var completions = DocumentService.GetCompletions(source, pos);

        completions.ShouldContain("x");
    }

    [Test]
    public void GetCompletions_at_position_inside_comparison_expression_should_include_bound_variable()
    {
        const string source = "let a = 1 in a >= 0";
        var pos = source.LastIndexOf('a');

        var completions = DocumentService.GetCompletions(source, pos);

        completions.ShouldContain("a");
    }

    [Test]
    public void GetCompletions_at_position_inside_equality_expression_should_include_bound_variable()
    {
        const string source = "let b = 1 in b == 1";
        var pos = source.LastIndexOf('b');

        var completions = DocumentService.GetCompletions(source, pos);

        completions.ShouldContain("b");
    }

    [Test]
    public void GetCompletions_at_position_inside_not_equal_expression_should_include_bound_variable()
    {
        const string source = "let c = 1 in c != 0";
        var pos = source.LastIndexOf('c');

        var completions = DocumentService.GetCompletions(source, pos);

        completions.ShouldContain("c");
    }

    [Test]
    public void GetCompletions_at_position_inside_multiply_expression_should_include_bound_variable()
    {
        const string source = "let n = 3 in n * 2";
        var pos = source.LastIndexOf('n');

        var completions = DocumentService.GetCompletions(source, pos);

        completions.ShouldContain("n");
    }

    [Test]
    public void GetCompletions_at_position_inside_divide_expression_should_include_bound_variable()
    {
        const string source = "let d = 8 in d / 2";
        var pos = source.LastIndexOf('d');

        var completions = DocumentService.GetCompletions(source, pos);

        completions.ShouldContain("d");
    }

    [Test]
    public void GetCompletions_at_position_inside_subtract_expression_should_include_bound_variable()
    {
        const string source = "let n = 5 in n - 1";
        var pos = source.LastIndexOf('n');

        var completions = DocumentService.GetCompletions(source, pos);

        completions.ShouldContain("n");
    }

    [Test]
    public void GetCompletions_at_position_inside_less_or_equal_expression_should_include_bound_variable()
    {
        const string source = "let n = 5 in n <= 10";
        var pos = source.LastIndexOf('n');

        var completions = DocumentService.GetCompletions(source, pos);

        completions.ShouldContain("n");
    }

    // ── GetDefinition inside binary expressions ─────────────────────────

    [Test]
    public void GetDefinition_should_resolve_variable_in_add_expression()
    {
        var root = CreateTempDir();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "let x = 1 in x + 2";
            File.WriteAllText(mainPath, source);
            var xInBody = source.LastIndexOf('x');

            var definition = DocumentService.GetDefinition(source, xInBody, mainPath);

            definition.ShouldNotBeNull();
            definition.Value.Start.ShouldBe(source.IndexOf("let x", StringComparison.Ordinal) + 4);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_resolve_variable_in_comparison_expression()
    {
        var root = CreateTempDir();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "let a = 1 in a >= 0";
            File.WriteAllText(mainPath, source);
            var aInBody = source.LastIndexOf('a');

            var definition = DocumentService.GetDefinition(source, aInBody, mainPath);

            definition.ShouldNotBeNull();
            definition.Value.Start.ShouldBe(source.IndexOf("let a", StringComparison.Ordinal) + 4);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_resolve_variable_in_if_condition()
    {
        var root = CreateTempDir();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "let flag = true in if flag then 1 else 0";
            File.WriteAllText(mainPath, source);
            var flagInCond = source.LastIndexOf("flag", StringComparison.Ordinal);

            var definition = DocumentService.GetDefinition(source, flagInCond, mainPath);

            definition.ShouldNotBeNull();
            definition.Value.Start.ShouldBe(source.IndexOf("flag", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_resolve_variable_in_if_then_branch()
    {
        var root = CreateTempDir();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "let y = 42 in if true then y else 0";
            File.WriteAllText(mainPath, source);
            var yInThen = source.LastIndexOf('y');

            var definition = DocumentService.GetDefinition(source, yInThen, mainPath);

            definition.ShouldNotBeNull();
            definition.Value.Start.ShouldBe(source.IndexOf("let y", StringComparison.Ordinal) + 4);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_resolve_variable_in_let_result_binding_body()
    {
        var root = CreateTempDir();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source =
                "type Result(E, A) = | Ok(A) | Error(E)\n" +
                "let? value = Ok(1) in Ok(value)";
            File.WriteAllText(mainPath, source);
            var valueInBody = source.LastIndexOf("value", StringComparison.Ordinal);

            var definition = DocumentService.GetDefinition(source, valueInBody, mainPath);

            definition.ShouldNotBeNull();
            var valueInBinding = source.IndexOf("value", StringComparison.Ordinal);
            definition.Value.Start.ShouldBe(valueInBinding);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_resolve_variable_in_let_rec_body()
    {
        var root = CreateTempDir();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "let rec f = fun (x) -> if x == 0 then 1 else f(x) in f(3)";
            File.WriteAllText(mainPath, source);
            var fInCall = source.LastIndexOf("f(3)");

            var definition = DocumentService.GetDefinition(source, fInCall, mainPath);

            definition.ShouldNotBeNull();
            definition.Value.Start.ShouldBe(source.IndexOf("let rec f", StringComparison.Ordinal) + 8);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_resolve_variable_in_call_argument()
    {
        var root = CreateTempDir();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "let g = fun (x) -> x in let z = 7 in g(z)";
            File.WriteAllText(mainPath, source);
            var zInArg = source.LastIndexOf('z');

            var definition = DocumentService.GetDefinition(source, zInArg, mainPath);

            definition.ShouldNotBeNull();
            definition.Value.Start.ShouldBe(source.IndexOf("let z", StringComparison.Ordinal) + 4);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_resolve_variable_in_cons_expression_head()
    {
        var root = CreateTempDir();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "let h = 1 in h :: []";
            File.WriteAllText(mainPath, source);
            var hInCons = source.LastIndexOf('h');

            var definition = DocumentService.GetDefinition(source, hInCons, mainPath);

            definition.ShouldNotBeNull();
            definition.Value.Start.ShouldBe(source.IndexOf("let h", StringComparison.Ordinal) + 4);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── ResolveDefinitionInPattern: Cons and Tuple branches ────────────

    [Test]
    public void GetDefinition_should_resolve_variable_in_cons_pattern_tail_position()
    {
        var root = CreateTempDir();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "match [1, 2] with | x :: xs -> xs";
            File.WriteAllText(mainPath, source);
            var xsInPattern = source.IndexOf("xs", StringComparison.Ordinal);

            var definition = DocumentService.GetDefinition(source, xsInPattern, mainPath);

            definition.ShouldNotBeNull();
            definition.Value.Start.ShouldBe(xsInPattern);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_resolve_variable_in_cons_pattern_head_position()
    {
        var root = CreateTempDir();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "match [1, 2] with | x :: xs -> x";
            File.WriteAllText(mainPath, source);
            var xInPattern = source.IndexOf("x ::", StringComparison.Ordinal);

            var definition = DocumentService.GetDefinition(source, xInPattern, mainPath);

            definition.ShouldNotBeNull();
            definition.Value.Start.ShouldBe(xInPattern);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_resolve_variable_in_tuple_pattern()
    {
        var root = CreateTempDir();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "match (1, 2) with | (a, b) -> a";
            File.WriteAllText(mainPath, source);
            var aInPattern = source.IndexOf("(a, b)", StringComparison.Ordinal) + 1;

            var definition = DocumentService.GetDefinition(source, aInPattern, mainPath);

            definition.ShouldNotBeNull();
            definition.Value.Start.ShouldBe(aInPattern);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── ValidateStandaloneImports unknown Ashes module ──────────────────

    [Test]
    public void Analyze_should_report_unknown_ashes_standard_module()
    {
        const string source = "import Ashes.UnknownModule\n42";

        var diagnostics = DocumentService.Analyze(source);

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].Message.ShouldContain("Unknown standard library module");
        diagnostics[0].Message.ShouldContain("Ashes.UnknownModule");
    }

    // ── GetHover null return paths ──────────────────────────────────────

    [Test]
    public void GetHover_should_return_null_when_source_has_import_errors()
    {
        // An unknown Ashes standard module produces import diagnostics,
        // causing GetHover to return null without attempting type resolution.
        const string source = "import Ashes.Undefined\n42";

        var hover = DocumentService.GetHover(source, source.LastIndexOf('4'));

        hover.ShouldBeNull();
    }

    [Test]
    public void GetHover_should_return_null_for_position_past_end_of_source()
    {
        const string source = "42";

        var hover = DocumentService.GetHover(source, source.Length + 100);

        hover.ShouldBeNull();
    }

    // ── GetDefinition null return paths ────────────────────────────────

    [Test]
    public void GetDefinition_should_return_null_for_source_with_parse_errors()
    {
        const string source = "if true then 1";

        var definition = DocumentService.GetDefinition(source, 5, null);

        definition.ShouldBeNull();
    }

    [Test]
    public void GetDefinition_should_return_null_for_position_before_header_offset()
    {
        const string source = "import Ashes.IO\nlet x = 1 in x";

        // Position -1 relative to the header-stripped source; negative strippedPosition
        var definition = DocumentService.GetDefinition(source, -5, null);

        definition.ShouldBeNull();
    }

    // ── Nested let binding resolution in imported module ────────────────

    [Test]
    public void GetDefinition_should_resolve_nested_let_binding_in_imported_module()
    {
        // Math.ash has an outer `let helper` and an inner `let add`.
        // Looking up Math.add requires TryFindBindingDefinition to recurse
        // past the outer let (name mismatch) into its body.
        var root = CreateTempProjectDir("let helper = 1 in let add = fun (x) -> x + helper in add");
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "import Math\nAshes.IO.print(Math.add(1))";
            File.WriteAllText(mainPath, source);
            var addPos = source.IndexOf("add", StringComparison.Ordinal);

            var definition = DocumentService.GetDefinition(source, addPos, mainPath);

            definition.ShouldNotBeNull();
            // 'add' is defined in Math.ash at "let add" (after "let helper = 1 in ")
            var mathSource = "let helper = 1 in let add = fun (x) -> x + helper in add";
            definition.Value.Start.ShouldBe(mathSource.IndexOf("let add", StringComparison.Ordinal) + 4);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashes_lsp_cov_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateTempProjectDir(string mathAshSource)
    {
        var root = Path.Combine(Path.GetTempPath(), "ashes_lsp_cov_proj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
        File.WriteAllText(Path.Combine(root, "Math.ash"), mathAshSource);
        return root;
    }
}
