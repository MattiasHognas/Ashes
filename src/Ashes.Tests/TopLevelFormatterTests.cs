using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Exact-output and idempotence tests for formatting a <see cref="Program"/> made of flat top-level
/// declarations (<c>let</c> / <c>let rec ... and ...</c> / <c>type</c>) plus an optional trailing
/// expression. One blank line separates adjacent items and the trailing expression; a rec group is a
/// single block; nested <c>let ... in</c> pyramids are preserved (never flattened).
/// </summary>
public sealed class TopLevelFormatterTests
{
    private static string Format(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.Errors.ShouldBeEmpty();

        return Ashes.Formatter.Formatter.Format(program);
    }

    [Test]
    public void Flat_let_declarations_are_separated_by_one_blank_line()
    {
        var formatted = Format("let a = 1\nlet b = 2\n");

        formatted.ShouldBe("let a = 1\n\nlet b = 2\n");
    }

    [Test]
    public void Let_declaration_is_separated_from_a_trailing_expression_by_one_blank_line()
    {
        // Constructed directly: the parser would otherwise glue a bare trailing atom onto the
        // preceding declaration's value via whitespace application. The formatter must still emit a
        // single blank line between a top-level declaration and the trailing expression.
        var program = new Program(
            new TopLevelItem[] { new TopLevelItem.LetDecl("a", new Expr.IntLit(1), IsRecursive: false) },
            new Expr.Var("b"));

        var formatted = Ashes.Formatter.Formatter.Format(program);

        formatted.ShouldBe("let a = 1\n\nb\n");
    }

    [Test]
    public void Type_declaration_is_separated_from_a_trailing_expression_by_one_blank_line()
    {
        var formatted = Format("type Color =\n    | Red\n    | Green\nRed\n");

        formatted.ShouldBe("type Color =\n    | Red\n    | Green\n\nRed\n");
    }

    [Test]
    public void Consecutive_type_declarations_are_separated_by_one_blank_line()
    {
        var formatted = Format("type A =\n    | X\ntype B =\n    | Y\nX\n");

        formatted.ShouldBe("type A =\n    | X\n\ntype B =\n    | Y\n\nX\n");
    }

    [Test]
    public void Let_declaration_and_a_following_extern_are_separated_by_one_blank_line()
    {
        // `extern` is a top-level declaration like any other: a preceding `let` is separated from it
        // by exactly one blank line (the grouping exception below applies only between two externs).
        var formatted = Format("let f = 1\nextern strlen(Str) -> Int\nf\n");

        formatted.ShouldBe("let f = 1\n\nextern strlen(Str) -> Int\n\nf\n");
    }

    [Test]
    public void Consecutive_extern_declarations_stay_grouped_as_a_block()
    {
        // FFI declaration blocks keep adjacent `extern` lines together with no blank line between
        // them, separated from the trailing expression by one blank line (matches FormatterTests).
        var formatted = Format("extern type Handle\nextern makeHandle(Int) -> Handle\n0\n");

        formatted.ShouldBe("extern type Handle\nextern makeHandle(Int) -> Handle\n\n0\n");
    }

    [Test]
    public void Interleaved_type_let_and_trailing_let_in_are_each_blank_line_separated()
    {
        var formatted = Format("type Color =\n    | Red\n    | Green\nlet c = Red\nlet r = c in r\n");

        formatted.ShouldBe("type Color =\n    | Red\n    | Green\n\nlet c = Red\n\nlet r = c\nin r\n");
    }

    [Test]
    public void Top_level_let_declaration_has_no_trailing_in()
    {
        var formatted = Format("let a = 1\nlet z = a in z\n");

        formatted.ShouldBe("let a = 1\n\nlet z = a\nin z\n");
    }

    [Test]
    public void Single_recursive_let_declaration_renders_without_in()
    {
        var formatted = Format("let rec loop = 1\n");

        formatted.ShouldBe("let rec loop = 1\n");
    }

    [Test]
    public void Rec_group_formats_as_a_single_block_with_each_and_at_let_indentation()
    {
        var formatted = Format("let rec a = 1\nand b = 2\n");

        formatted.ShouldBe("let rec a = 1\nand b = 2\n");
    }

    [Test]
    public void Rec_group_with_three_members_formats_as_a_single_block()
    {
        var formatted = Format("let rec a = 1\nand b = 2\nand c = 3\n");

        formatted.ShouldBe("let rec a = 1\nand b = 2\nand c = 3\n");
    }

    [Test]
    public void Rec_group_with_multiline_values_keeps_each_and_on_its_own_line()
    {
        var formatted = Format("let rec even = fun (n) -> n\nand odd = fun (n) -> n\n");

        formatted.ShouldBe("let rec even = \n    fun (n) -> n\nand odd = \n    fun (n) -> n\n");
    }

    [Test]
    public void Nested_let_in_pyramid_is_preserved_and_not_flattened()
    {
        var formatted = Format("let x = let y = 1 in y in x\n");

        formatted.ShouldBe("let x = \n    let y = 1\n    in y\nin x\n");
    }

    [Test]
    public void Parenthesized_let_value_in_a_flat_declaration_stays_a_declaration()
    {
        var formatted = Format("let b = (let y = 2 in y)\n");

        formatted.ShouldBe("let b = \n    (let y = 2\n    in y)\n");
    }

    [Test]
    public void Single_expression_program_formats_as_before()
    {
        var formatted = Format("1 + 2\n");

        formatted.ShouldBe("1 + 2\n");
    }

    [Test]
    [Arguments("let a = 1\nlet b = 2\n")]
    [Arguments("let a = 1\nlet z = a in z\n")]
    [Arguments("type Color =\n    | Red\n    | Green\nRed\n")]
    [Arguments("type A =\n    | X\ntype B =\n    | Y\nX\n")]
    [Arguments("let f = 1\nextern strlen(Str) -> Int\nf\n")]
    [Arguments("extern type Handle\nextern makeHandle(Int) -> Handle\n0\n")]
    [Arguments("type Color =\n    | Red\n    | Green\nlet c = Red\nlet r = c in r\n")]
    [Arguments("let rec a = 1\nand b = 2\n")]
    [Arguments("let rec a = 1\nand b = 2\nand c = 3\n")]
    [Arguments("let rec even = fun (n) -> n\nand odd = fun (n) -> n\n")]
    [Arguments("let x = let y = 1 in y in x\n")]
    [Arguments("let b = (let y = 2 in y)\n")]
    [Arguments("1 + 2\n")]
    public void Formatting_is_idempotent(string source)
    {
        var once = Format(source);

        // Re-parsing and re-formatting the canonical output must reproduce it exactly.
        var twice = Format(once);

        twice.ShouldBe(once);
    }
}
