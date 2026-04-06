using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class IsIrrefutableLetPatternTests
{
    // ────── Irrefutable patterns (should return true) ──────

    [Test]
    public void Var_pattern_is_irrefutable()
    {
        Parser.IsIrrefutableLetPattern(new Pattern.Var("x")).ShouldBeTrue();
    }

    [Test]
    public void Wildcard_pattern_is_irrefutable()
    {
        Parser.IsIrrefutableLetPattern(new Pattern.Wildcard()).ShouldBeTrue();
    }

    [Test]
    public void Tuple_of_vars_is_irrefutable()
    {
        var pattern = new Pattern.Tuple([new Pattern.Var("a"), new Pattern.Var("b")]);
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeTrue();
    }

    [Test]
    public void Tuple_of_wildcards_is_irrefutable()
    {
        var pattern = new Pattern.Tuple([new Pattern.Wildcard(), new Pattern.Wildcard()]);
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeTrue();
    }

    [Test]
    public void Tuple_with_mixed_var_and_wildcard_is_irrefutable()
    {
        var pattern = new Pattern.Tuple([new Pattern.Var("a"), new Pattern.Wildcard()]);
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeTrue();
    }

    [Test]
    public void Nested_tuple_of_vars_is_irrefutable()
    {
        var inner = new Pattern.Tuple([new Pattern.Var("x"), new Pattern.Var("y")]);
        var pattern = new Pattern.Tuple([inner, new Pattern.Var("z")]);
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeTrue();
    }

    [Test]
    public void Cons_of_vars_is_irrefutable()
    {
        var pattern = new Pattern.Cons(new Pattern.Var("head"), new Pattern.Var("tail"));
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeTrue();
    }

    [Test]
    public void Cons_with_wildcard_tail_is_irrefutable()
    {
        var pattern = new Pattern.Cons(new Pattern.Var("head"), new Pattern.Wildcard());
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeTrue();
    }

    [Test]
    public void Single_element_tuple_of_var_is_irrefutable()
    {
        var pattern = new Pattern.Tuple([new Pattern.Var("a")]);
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeTrue();
    }

    [Test]
    public void Empty_tuple_is_irrefutable()
    {
        var pattern = new Pattern.Tuple([]);
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeTrue();
    }

    // ────── Refutable patterns (should return false) ──────

    [Test]
    public void IntLit_pattern_is_refutable()
    {
        Parser.IsIrrefutableLetPattern(new Pattern.IntLit(0)).ShouldBeFalse();
    }

    [Test]
    public void StrLit_pattern_is_refutable()
    {
        Parser.IsIrrefutableLetPattern(new Pattern.StrLit("hello")).ShouldBeFalse();
    }

    [Test]
    public void BoolLit_true_pattern_is_refutable()
    {
        Parser.IsIrrefutableLetPattern(new Pattern.BoolLit(true)).ShouldBeFalse();
    }

    [Test]
    public void BoolLit_false_pattern_is_refutable()
    {
        Parser.IsIrrefutableLetPattern(new Pattern.BoolLit(false)).ShouldBeFalse();
    }

    [Test]
    public void Constructor_pattern_is_refutable()
    {
        var pattern = new Pattern.Constructor("Some", [new Pattern.Var("x")]);
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeFalse();
    }

    [Test]
    public void EmptyList_pattern_is_refutable()
    {
        Parser.IsIrrefutableLetPattern(new Pattern.EmptyList()).ShouldBeFalse();
    }

    [Test]
    public void Tuple_containing_intlit_is_refutable()
    {
        var pattern = new Pattern.Tuple([new Pattern.Var("a"), new Pattern.IntLit(1)]);
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeFalse();
    }

    [Test]
    public void Tuple_containing_strlit_is_refutable()
    {
        var pattern = new Pattern.Tuple([new Pattern.StrLit("x"), new Pattern.Var("b")]);
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeFalse();
    }

    [Test]
    public void Tuple_containing_boollit_is_refutable()
    {
        var pattern = new Pattern.Tuple([new Pattern.Var("a"), new Pattern.BoolLit(false)]);
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeFalse();
    }

    [Test]
    public void Tuple_containing_constructor_is_refutable()
    {
        var pattern = new Pattern.Tuple([new Pattern.Constructor("None", []), new Pattern.Var("b")]);
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeFalse();
    }

    [Test]
    public void Nested_tuple_with_refutable_inner_is_refutable()
    {
        var inner = new Pattern.Tuple([new Pattern.Var("x"), new Pattern.IntLit(42)]);
        var pattern = new Pattern.Tuple([inner, new Pattern.Var("z")]);
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeFalse();
    }

    [Test]
    public void Cons_with_intlit_head_is_refutable()
    {
        var pattern = new Pattern.Cons(new Pattern.IntLit(1), new Pattern.Var("tail"));
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeFalse();
    }

    [Test]
    public void Cons_with_empty_list_tail_is_refutable()
    {
        var pattern = new Pattern.Cons(new Pattern.Var("head"), new Pattern.EmptyList());
        Parser.IsIrrefutableLetPattern(pattern).ShouldBeFalse();
    }

    // ────── Integration: parser rejects refutable let-patterns ──────

    [Test]
    public void Let_tuple_pattern_parses_without_error()
    {
        var diag = new Diagnostics();
        new Parser("let (a, b) = (1, 2) in a + b", diag).ParseExpression();
        diag.Errors.Count.ShouldBe(0);
    }

    [Test]
    public void Let_tuple_with_intlit_reports_refutable_error()
    {
        var diag = new Diagnostics();
        new Parser("let (0, b) = (0, 2) in b", diag).ParseExpression();
        diag.Errors.Count.ShouldBeGreaterThan(0);
        diag.Errors[0].ShouldContain("Refutable pattern");
    }

    [Test]
    public void Let_tuple_with_strlit_reports_refutable_error()
    {
        var diag = new Diagnostics();
        new Parser("let (\"x\", b) = (\"x\", 2) in b", diag).ParseExpression();
        diag.Errors.Count.ShouldBeGreaterThan(0);
        diag.Errors[0].ShouldContain("Refutable pattern");
    }

    [Test]
    public void Let_tuple_with_boollit_reports_refutable_error()
    {
        var diag = new Diagnostics();
        new Parser("let (true, b) = (true, 2) in b", diag).ParseExpression();
        diag.Errors.Count.ShouldBeGreaterThan(0);
        diag.Errors[0].ShouldContain("Refutable pattern");
    }

    [Test]
    public void Let_tuple_with_wildcard_parses_without_error()
    {
        var diag = new Diagnostics();
        new Parser("let (_, b) = (1, 2) in b", diag).ParseExpression();
        diag.Errors.Count.ShouldBe(0);
    }

    [Test]
    public void Let_nested_tuple_parses_without_error()
    {
        var diag = new Diagnostics();
        new Parser("let ((a, b), c) = ((1, 2), 3) in a + b + c", diag).ParseExpression();
        diag.Errors.Count.ShouldBe(0);
    }
}
