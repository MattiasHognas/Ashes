using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class MatchTypingTests
{
    [Test]
    public void Match_with_option_constructors_typechecks_without_error()
    {
        var (_, diag) = LowerProgram(
            """
            type LocalMaybe = | None | Some(T)
            let unwrapOr = fun (opt, def) ->
              match opt with
              | None -> def
              | Some(x) -> x
            in Ashes.IO.print(unwrapOr(Some(10), 0))
            """);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Match_with_mixed_adt_constructors_reports_error()
    {
        var (_, diag) = LowerProgram(
            """
            type LocalMaybe = | None | Some(T)
            type Result = | Ok(T) | Error(T)
            match None with
            | None -> 0
            | Ok(v) -> v
            """);

        diag.Errors.ShouldContain(x => x.Contains("Constructor patterns from different ADTs", StringComparison.Ordinal));
    }

    [Test]
    public void Match_with_result_constructors_typechecks_without_error()
    {
        var (_, diag) = LowerProgram(
            """
            let resTag = match Error(1) with
              | Ok(x) -> 1
              | Error(x) -> 2
            in Ashes.IO.print(resTag)
            """);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Match_with_tuple_pattern_typechecks_without_error()
    {
        var (_, diag) = LowerProgram(
            """
            let p = (1, 2)
            in
            match p with
            | (a, b) -> a + b
            """);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Match_pattern_with_unknown_constructor_reports_improved_message()
    {
        var (_, diag) = LowerProgram(
            """
            type LocalMaybe = | None | Some(T)
            match None with
            | Foo(x) -> x
            | None -> 0
            """);

        diag.Errors.ShouldContain(x => x.Contains("Unknown constructor 'Foo' in pattern", StringComparison.Ordinal));
        diag.Errors.ShouldContain(x => x.Contains("Did you mean:", StringComparison.Ordinal));
    }

    [Test]
    public void Match_pattern_with_wrong_arity_reports_shape_hint()
    {
        var (_, diag) = LowerProgram(
            """
            type LocalMaybe = | None | Some(T)
            match Some(1) with
            | Some(x, y) -> x
            | None -> 0
            """);

        diag.Errors.ShouldContain(x =>
            x.Contains("Constructor 'Some' expects 1 argument(s) but pattern has 2", StringComparison.Ordinal) &&
            x.Contains("Expected shape: Some(T)", StringComparison.Ordinal));
    }

    [Test]
    public void Match_with_all_constructors_of_adt_is_exhaustive()
    {
        var (_, diag) = LowerProgram(
            """
            type Color = | Red | Green | Blue
            let c = Green
            in
            match c with
            | Red -> 1
            | Green -> 2
            | Blue -> 3
            """);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Match_with_empty_and_cons_list_patterns_is_exhaustive()
    {
        var (_, diag) = LowerProgram(
            """
            let xs = [1,2,3]
            in
            match xs with
            | [] -> 0
            | x :: rest -> 1
            """);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Match_with_list_patterns_missing_cons_reports_case()
    {
        var (_, diag) = LowerProgram(
            """
            let xs = [1]
            in
            match xs with
            | [] -> 0
            """);

        diag.Errors.ShouldContain(x => x.Contains("Non-exhaustive match expression. Missing case: x :: xs.", StringComparison.Ordinal));
    }

    [Test]
    public void Match_with_list_patterns_missing_empty_reports_case()
    {
        var (_, diag) = LowerProgram(
            """
            let xs = []
            in
            match xs with
            | x :: rest -> 1
            """);

        diag.Errors.ShouldContain(x => x.Contains("Non-exhaustive match expression. Missing case: [].", StringComparison.Ordinal));
    }

    [Test]
    public void Match_with_duplicate_constructor_arm_reports_unreachable_arm_error()
    {
        var (_, diag) = LowerProgram(
            """
            type Color = | Red | Green | Blue
            let c = Red
            in
            match c with
            | Red -> 1
            | Red -> 2
            | Green -> 3
            | Blue -> 4
            """);

        diag.Errors.ShouldContain(x => x.Contains("Unreachable match arm: constructor Red is already matched earlier.", StringComparison.Ordinal));
    }

    [Test]
    public void Match_arms_after_wildcard_report_unreachable_arm_error()
    {
        var (_, diag) = LowerProgram(
            """
            type Color = | Red | Green | Blue
            let c = Red
            in
            match c with
            | _ -> 1
            | Red -> 2
            """);

        diag.Errors.ShouldContain(x => x.Contains("Unreachable match arm: a catch-all pattern was already matched earlier.", StringComparison.Ordinal));
    }

    [Test]
    public void Match_missing_adt_constructors_reports_non_exhaustive_error()
    {
        var (_, diag) = LowerProgram(
            """
            type Color = | Red | Green | Blue
            let c = Green
            in
            match c with
            | Red -> 1
            """);

        diag.Errors.ShouldContain(x =>
            x.Contains("Non-exhaustive match expression.", StringComparison.Ordinal) &&
            x.Contains("'Green'", StringComparison.Ordinal) &&
            x.Contains("'Blue'", StringComparison.Ordinal));
    }

    [Test]
    public void Match_with_unknown_constructor_pattern_does_not_add_generic_non_exhaustive_error()
    {
        var (_, diag) = LowerProgram(
            """
            match 1 with
            | Missing(x) -> x
            """);

        diag.Errors.ShouldContain(x => x.Contains("Unknown constructor 'Missing' in pattern", StringComparison.Ordinal));
        diag.Errors.ShouldNotContain(x => x.Contains("Non-exhaustive match expression.", StringComparison.Ordinal));
    }

    [Test]
    public void Match_missing_result_error_branch_reports_result_specific_message()
    {
        var (_, diag) = LowerProgram(
            """
            match Ok(1) with
            | Ok(x) -> x
            """);

        diag.Errors.ShouldContain(x => x.Contains("Non-exhaustive match on Result: missing Error.", StringComparison.Ordinal));
    }

    [Test]
    public void Match_with_both_result_branches_does_not_report_result_specific_message()
    {
        var (_, diag) = LowerProgram(
            """
            match Ok(1) with
            | Ok(x) -> x
            | Error(_) -> 0
            """);

        diag.Errors.ShouldNotContain(x => x.Contains("Non-exhaustive match on Result", StringComparison.Ordinal));
    }

    [Test]
    public void Match_with_print_calls_of_different_argument_types_typechecks_without_error()
    {
        var (_, diag) = LowerProgram(
            """
            import Ashes.File
            match Ashes.File.exists("out.txt") with
            | Ok(found) ->
                if found
                then Ashes.IO.print(1)
                else Ashes.IO.print(0)
            | Error(msg) -> Ashes.IO.print(msg)
            """);

        diag.Errors.ShouldBeEmpty();
    }

    private static (Lowering Lowering, Diagnostics Diag) LowerProgram(string source)
    {
        var diag = new Diagnostics();
        var parsed = ProjectSupport.ParseImportHeader(source, "<memory>");
        var layout = ProjectSupport.BuildStandaloneCompilationLayout(parsed.SourceWithoutImports, parsed.ImportNames);
        var program = new Parser(layout.Source, diag).ParseProgram();
        var lowering = new Lowering(diag);
        lowering.Lower(program);
        return (lowering, diag);
    }
}
