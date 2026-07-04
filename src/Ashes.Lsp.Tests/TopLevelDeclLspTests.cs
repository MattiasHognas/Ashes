using Ashes.Frontend;
using Shouldly;

namespace Ashes.Lsp.Tests;

/// <summary>
/// Exercises the LSP document model against files that use the flat top-level declaration surface
/// (Model-A sequential scoping): diagnostics for the new top-level errors flow through with correct
/// spans, hover/completion enumerate top-level <c>let</c>/<c>type</c> names as file-scope symbols,
/// and formatting through the LSP matches what the CLI's formatter produces. The LSP stays a pure
/// consumer of Frontend/Semantics/Formatter — it never depends on Backend.
/// </summary>
public sealed class TopLevelDeclLspTests
{
    private const string DuplicateTopLevelBinding = "ASH013";
    private const string ForwardReference = "ASH014";
    private const string ConflictingImportSelectors = "ASH016";

    [Test]
    public void Analyze_should_surface_duplicate_top_level_binding_at_the_redeclared_value()
    {
        const string source = "let foo = 1\nlet foo = 2";
        var diagnostics = DocumentService.Analyze(source);

        var duplicate = diagnostics.ShouldHaveSingleItem();
        duplicate.Code.ShouldBe(DuplicateTopLevelBinding);
        duplicate.Message.ShouldContain("Duplicate top-level binding 'foo'");

        // The span points at the offending second value (the `2` literal), not the first declaration.
        var expected = source.LastIndexOf('2');
        duplicate.Start.ShouldBe(expected);
        duplicate.End.ShouldBe(expected + 1);
    }

    [Test]
    public void Analyze_should_surface_forward_reference_at_the_unresolved_use()
    {
        const string source = "let a = b\nlet b = 1";
        var diagnostics = DocumentService.Analyze(source);

        var forward = diagnostics.ShouldHaveSingleItem();
        forward.Code.ShouldBe(ForwardReference);
        forward.Message.ShouldContain("not yet declared");

        // The span points at the forward use of `b` inside `a`'s value.
        var expected = source.IndexOf("= b", StringComparison.Ordinal) + 2;
        forward.Start.ShouldBe(expected);
        forward.End.ShouldBe(expected + 1);
    }

    [Test]
    public void Analyze_should_surface_conflicting_unqualified_import_selectors()
    {
        const string source = "import Ashes.List.map\nimport Ashes.Maybe.map\n0";
        var diagnostics = DocumentService.Analyze(source);

        var conflict = diagnostics.ShouldHaveSingleItem();
        conflict.Code.ShouldBe(ConflictingImportSelectors);
        conflict.Message.ShouldContain("Conflicting unqualified import selectors for 'map'");

        // The span points at the second (conflicting) import line.
        var secondImportStart = source.IndexOf("import Ashes.Maybe.map", StringComparison.Ordinal);
        conflict.Start.ShouldBe(secondImportStart);
        conflict.End.ShouldBe(secondImportStart + "import Ashes.Maybe.map".Length);
    }

    [Test]
    public void Hover_should_report_type_for_top_level_let_binding()
    {
        // Hover a use of the top-level binding in a later declaration's value (Model-A visibility).
        const string source = "let answer = 42\nlet alias = answer";
        var hover = DocumentService.GetHover(source, source.LastIndexOf("answer", StringComparison.Ordinal));

        hover.ShouldNotBeNull();
        hover.Value.Contents.ShouldBe("answer : Int");
    }

    [Test]
    public void Hover_should_report_type_for_mutually_recursive_group_member()
    {
        const string source =
            "let recursive isEven = given (n) -> if n == 0 then true else isOdd(n - 1)\n"
            + "and isOdd = given (n) -> if n == 0 then false else isEven(n - 1)\n"
            + "isEven(4)";
        var hover = DocumentService.GetHover(source, source.IndexOf("isOdd", StringComparison.Ordinal));

        hover.ShouldNotBeNull();
        hover.Value.Contents.ShouldBe("isOdd : Int -> Bool");
    }

    [Test]
    public void Hover_should_report_type_for_a_top_level_type_constructor()
    {
        // A top-level `type` declaration's constructor is a file-scope symbol; hovering its use in the
        // trailing expression reports the ADT it belongs to.
        const string source = "type Color = | Red | Green\nRed";
        var hover = DocumentService.GetHover(source, source.LastIndexOf("Red", StringComparison.Ordinal));

        hover.ShouldNotBeNull();
        hover.Value.Contents.ShouldContain("Color");
    }

    [Test]
    public void Completion_should_include_top_level_let_and_type_names()
    {
        const string source = "type Color = | Red | Green\nlet answer = 42";
        var completions = DocumentService.GetCompletions(source, source.Length, filePath: null);

        completions.ShouldContain("answer"); // top-level let
        completions.ShouldContain("Color");  // top-level type name
        completions.ShouldContain("Red");    // constructor of the top-level type
        completions.ShouldContain("Green");
    }

    [Test]
    public void Completion_should_expose_earlier_top_level_binding_inside_a_later_declaration_value()
    {
        // Model-A: a binding is visible to the values of subsequent declarations.
        const string source = "let basis = 10\nlet next = basis";
        var position = source.IndexOf("= basis", StringComparison.Ordinal) + 2;
        var completions = DocumentService.GetCompletions(source, position, filePath: null);

        completions.ShouldContain("basis");
    }

    [Test]
    public void Format_should_match_the_cli_formatter_for_top_level_decl_files()
    {
        // The CLI's `fmt` parses the source and calls Ashes.Formatter.Formatter.Format on the program.
        // Re-run that exact path here and assert the LSP's Format delegates to it identically (parity),
        // rather than comparing against a frozen literal.
        const string source = "type Color =\n| Red\n| Green\nlet  answer=40+2\nlet recursive loop = given (n) -> if n == 0 then 0 else loop(n - 1)\nanswer";

        var options = new global::Ashes.Formatter.FormattingOptions { NewLine = "\n" };
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.Errors.Count.ShouldBe(0);
        var expected = global::Ashes.Formatter.Formatter.Format(program, preferPipelines: false, options: options);

        var actual = DocumentService.Format(source, filePath: null);

        actual.ShouldBe(expected);
    }
}
