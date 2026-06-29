using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Canonical-output and round-trip tests for formatting brace-free records
/// (LANGUAGE_SPEC §4.1, FORMATTER_SPEC): declarations render one field per
/// <c>|</c> line, construction renders as a named-argument call, and updates
/// render with a brace-free <c>with</c>.
/// </summary>
public sealed class BraceFreeRecordFormatterTests
{
    [Test]
    public void Format_should_render_record_declaration_one_field_per_line()
    {
        var formatted = Format("type Point =\n    | x: Int\n    | y: Int\n0\n");

        formatted.ShouldBe("type Point =\n    | x: Int\n    | y: Int\n\n0\n");
    }

    [Test]
    public void Format_should_render_record_construction_as_named_arguments()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.RecordLit("Point", [("x", new Expr.IntLit(1)), ("y", new Expr.IntLit(2))]));

        formatted.ShouldBe("Point(x = 1, y = 2)\n");
    }

    [Test]
    public void Format_should_render_single_field_record_update()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.RecordUpdate(new Expr.Var("p"), [("x", new Expr.IntLit(5))]));

        formatted.ShouldBe("p with x = 5\n");
    }

    [Test]
    public void Format_should_render_multi_field_record_update()
    {
        var formatted = Ashes.Formatter.Formatter.Format(
            new Expr.RecordUpdate(new Expr.Var("p"), [("x", new Expr.IntLit(5)), ("y", new Expr.IntLit(6))]));

        formatted.ShouldBe("p with x = 5, y = 6\n");
    }

    [Test]
    public void Format_should_round_trip_a_full_record_program()
    {
        var source = "type Point =\n    | x: Int\n    | y: Int\nlet p = Point(x = 1, y = 2)\nin p.x\n";
        var formatted = Format(source);

        formatted.ShouldBe("type Point =\n    | x: Int\n    | y: Int\n\nlet p = Point(x = 1, y = 2)\nin p.x\n");
        // Idempotence: re-formatting the canonical output is a no-op.
        Format(formatted).ShouldBe(formatted);
    }

    [Test]
    public void Format_should_round_trip_a_record_update()
    {
        var formatted = Format("let q = p with x = 5\nin q\n");

        formatted.ShouldBe("let q = p with x = 5\nin q\n");
    }

    private static string Format(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.Errors.ShouldBeEmpty();

        return Ashes.Formatter.Formatter.Format(program);
    }
}
