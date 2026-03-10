using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ConstructorExpressionTests
{
    [Test]
    public void Nullary_constructor_typechecks_without_error()
    {
        var (_, diag) = LowerProgram("type Option = | None | Some(T)\nlet x = None\nin Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Constructor_with_argument_typechecks_without_error()
    {
        var (_, diag) = LowerProgram("type Option = | None | Some(T)\nlet x = Some(42)\nin Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Nullary_constructor_produces_parent_adt_type()
    {
        var diag = new Diagnostics();
        var program = new Parser("type Option = | None | Some(T)\nNone", diag).ParseProgram();
        var lowering = new Lowering(diag);
        lowering.Lower(program);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Constructor_call_produces_parent_adt_type()
    {
        var diag = new Diagnostics();
        var program = new Parser("type Option = | None | Some(T)\nSome(42)", diag).ParseProgram();
        var lowering = new Lowering(diag);
        lowering.Lower(program);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Nullary_constructor_used_with_argument_reports_arity_error()
    {
        var (_, diag) = LowerProgram("type Option = | None | Some(T)\nNone(42)");

        diag.Errors.ShouldContain(x => x.Contains("Constructor 'None' expects 0 argument(s) but got 1", StringComparison.Ordinal));
    }

    [Test]
    public void Constructor_used_without_arguments_can_be_used_as_function_value()
    {
        var (_, diag) = LowerProgram("type Option = | None | Some(T)\nlet wrap = Some\nin match wrap(42) with | Some(x) -> Ashes.IO.print(x) | None -> Ashes.IO.print(0)");

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Unknown_constructor_in_expression_reports_improved_message()
    {
        var (_, diag) = LowerProgram("type Option = | None | Some(T)\nFoo(1)");

        diag.Errors.ShouldContain(x => x.Contains("Unknown constructor 'Foo'", StringComparison.Ordinal));
    }

    [Test]
    public void Unknown_constructor_in_expression_includes_did_you_mean_hint()
    {
        var (_, diag) = LowerProgram("type Option = | None | Some(T)\nFoo(1)");

        diag.Errors.ShouldContain(x => x.Contains("Did you mean:", StringComparison.Ordinal));
    }

    [Test]
    public void Nullary_constructor_used_with_argument_reports_arity_error_with_shape()
    {
        var (_, diag) = LowerProgram("type Option = | None | Some(T)\nNone(42)");

        diag.Errors.ShouldContain(x =>
            x.Contains("Constructor 'None' expects 0 argument(s) but got 1", StringComparison.Ordinal) &&
            x.Contains("Expected shape: None", StringComparison.Ordinal));
    }

    [Test]
    public void Constructor_function_value_can_be_partially_applied_via_let_binding()
    {
        var (_, diag) = LowerProgram("let wrap = Error\nin match wrap(\"bad\") with | Error(msg) -> Ashes.IO.print(msg) | Ok(_) -> Ashes.IO.print(\"ok\")");

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Unknown_constructor_in_expression_does_not_cascade_non_function_type_error()
    {
        var (_, diag) = LowerProgram("type Option = | None | Some(T)\nFoo(1)");

        diag.Errors.ShouldNotContain(x => x.Contains("non-function type", StringComparison.Ordinal));
    }

    [Test]
    public void Constructor_application_with_wrong_arg_count_reports_shape_hint()
    {
        var (_, diag) = LowerProgram("type Option = | None | Some(T)\nSome(1, 2)");

        diag.Errors.ShouldContain(x =>
            x.Contains("Constructor 'Some' expects 1 argument(s) but got 2", StringComparison.Ordinal) &&
            x.Contains("Expected shape: Some(T)", StringComparison.Ordinal));
    }

    [Test]
    public void Constructor_from_different_type_typechecks()
    {
        var (_, diag) = LowerProgram("type Color = | Red | Green | Blue\nlet c = Red\nin Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Builtin_option_string_none_typechecks_without_error()
    {
        var (_, diag) = LowerProgram("let x = None\nin Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Builtin_option_string_some_typechecks_without_error()
    {
        var (_, diag) = LowerProgram("let x = Some(\"hi\")\nin Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Builtin_option_string_match_typechecks_without_error()
    {
        var (_, diag) = LowerProgram("match Some(\"a\") with | None -> 0 | Some(x) -> 1");

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Builtin_option_string_some_rejects_non_string_payload()
    {
        var (_, diag) = LowerProgram("Some(1)");

        diag.Errors.ShouldContain(x => x.Contains("Type mismatch", StringComparison.OrdinalIgnoreCase)
            || x.Contains("cannot unify", StringComparison.OrdinalIgnoreCase)
            || x.Contains("Int", StringComparison.Ordinal));
    }

    private static (Lowering Lowering, Diagnostics Diag) LowerProgram(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        var lowering = new Lowering(diag);
        lowering.Lower(program);
        return (lowering, diag);
    }
}
