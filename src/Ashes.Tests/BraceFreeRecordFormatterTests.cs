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

    [Test]
    public void Format_should_parenthesize_a_record_update_nested_in_a_call_argument()
    {
        var expression = new Expr.Call(
            new Expr.Var("async"),
            new Expr.Let(
                "updated",
                new Expr.RecordLit("Point", [("x", new Expr.IntLit(1)), ("y", new Expr.IntLit(2))]),
                new Expr.RecordUpdate(new Expr.Var("updated"), [("x", new Expr.IntLit(3))])));

        string formatted = Ashes.Formatter.Formatter.Format(expression);

        formatted.ShouldBe("async((let updated = Point(x = 1, y = 2)\nin updated with x = 3))\n");
        Format(formatted).ShouldBe(formatted);
    }

    [Test]
    public void Format_should_parenthesize_a_record_update_nested_in_a_match_scrutinee()
    {
        var expression = new Expr.Match(
            new Expr.Let(
                "updated",
                new Expr.RecordLit("Point", [("x", new Expr.IntLit(1)), ("y", new Expr.IntLit(2))]),
                new Expr.RecordUpdate(new Expr.Var("updated"), [("x", new Expr.IntLit(3))])),
            [new MatchCase(new Pattern.Constructor("Point", [new Pattern.Var("x"), new Pattern.Wildcard()]), new Expr.Var("x"))]);

        string formatted = Ashes.Formatter.Formatter.Format(expression);

        formatted.ShouldBe(
            "match (let updated = Point(x = 1, y = 2)\nin updated with x = 3) with\n    | Point(x, _) -> x\n");
        Format(formatted).ShouldBe(formatted);
    }

    // `with` takes every following `name = value` pair as its own field, so an update used as a
    // record-literal field value must keep its parentheses or it absorbs the literal's remaining
    // fields on the next parse.
    [Test]
    public void Format_should_parenthesize_a_record_update_used_as_a_record_literal_field()
    {
        const string source = "Value(state = (inner with currentSpan = previous), temp = temp)\n";

        string formatted = Format(source);

        formatted.ShouldBe(source);
        Format(formatted).ShouldBe(formatted);
    }

    [Test]
    public void Format_should_parenthesize_a_record_update_used_as_a_multiline_record_literal_field()
    {
        const string source = "Value(\n    state = (inner with currentSpan = previous),\n    temp = temp\n)\n";

        string formatted = Format(source);

        formatted.ShouldBe(source);
        Format(formatted).ShouldBe(formatted);
    }

    // The trailing body of a lambda or let is an unparenthesized right edge too.
    [Test]
    public void Format_should_parenthesize_a_lambda_ending_in_a_record_update_used_as_a_record_literal_field()
    {
        const string source = "Value(state = (given (current) -> current with span = next), temp = temp)\n";

        string formatted = Format(source);

        formatted.ShouldBe(source);
        Format(formatted).ShouldBe(formatted);
    }

    // Nothing follows the last field, so its own closing `)` already ends the update
    // unambiguously; no protective parentheses are needed (or added).
    [Test]
    public void Format_should_not_parenthesize_a_record_update_as_the_last_record_literal_field()
    {
        const string source = "Value(temp = temp, state = inner with currentSpan = previous)\n";

        string formatted = Format(source);

        formatted.ShouldBe(source);
        Format(formatted).ShouldBe(formatted);
    }

    [Test]
    public void Format_should_parenthesize_a_record_update_used_as_an_update_field_value()
    {
        const string source = "p with a = (q with b = 1), c = 2\n";

        string formatted = Format(source);

        formatted.ShouldBe(source);
        Format(formatted).ShouldBe(formatted);
    }

    private static string Format(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.Errors.ShouldBeEmpty();

        return Ashes.Formatter.Formatter.Format(program);
    }
}
