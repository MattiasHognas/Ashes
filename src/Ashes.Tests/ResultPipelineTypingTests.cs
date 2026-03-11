using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ResultPipelineTypingTests
{
    [Test]
    public void Result_pipe_with_pure_function_typechecks_without_error()
    {
        var (_, diag) = LowerProgram(
            """
            let x = Ok(3) |?> (fun (n) -> n + 1)
            in match x with
            | Ok(v) -> Ashes.IO.print(v)
            | Error(_) -> Ashes.IO.print(0)
            """);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Result_pipe_with_result_returning_function_typechecks_without_error()
    {
        var (_, diag) = LowerProgram(
            """
            let parse = fun (x) -> Ok(x + 1)
            in let y = Ok(41) |?> parse
            in match y with
            | Ok(v) -> Ashes.IO.print(v)
            | Error(_) -> Ashes.IO.print(0)
            """);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Result_map_error_pipe_typechecks_without_error()
    {
        var (_, diag) = LowerProgram(
            """
            type AppError = | Wrapped(String)
            let x = Error("boom") |!> Wrapped
            in match x with
            | Ok(_) -> Ashes.IO.print("ok")
            | Error(Wrapped(msg)) -> Ashes.IO.print(msg)
            """);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Mixed_result_pipeline_chain_typechecks_without_error()
    {
        var (_, diag) = LowerProgram(
            """
            type ParseError = | NotAnInt(String)
            type AppError = | Parse(ParseError)
            let parse = fun (x) -> if x == "41" then Ok(41) else Error(NotAnInt(x))
            in let y = Ok("41") |?> parse |?> (fun (n) -> n + 1) |!> Parse
            in match y with
            | Ok(v) -> Ashes.IO.print(v)
            | Error(Parse(NotAnInt(_))) -> Ashes.IO.print(0)
            """);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Nested_error_patterns_on_typed_result_typecheck_without_error()
    {
        var (_, diag) = LowerProgram(
            """
            type JsonError = | MissingField(String)
            type AppError = | Json(JsonError)
            let x = Error(Json(MissingField("age")))
            in match x with
            | Ok(_) -> Ashes.IO.print("ok")
            | Error(Json(MissingField(name))) -> Ashes.IO.print(name)
            """);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Let_result_binding_typechecks_without_error()
    {
        var (_, diag) = LowerProgram(
            """
            let x =
                let? n = Ok(42)
                in
                Ok(n + 1)
            in match x with
            | Ok(v) -> Ashes.IO.print(v)
            | Error(_) -> Ashes.IO.print(0)
            """);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Let_result_binding_requires_result_input()
    {
        var (_, diag) = LowerProgram(
            """
            let x =
                let? a = 42
                in
                Ok(a)
            in x
            """);

        diag.Errors.ShouldContain(x => x.Contains("let? requires a Result(E, A) expression.", StringComparison.Ordinal));
    }

    [Test]
    public void Let_result_binding_requires_result_body()
    {
        var (_, diag) = LowerProgram(
            """
            let x =
                let? a = Ok(42)
                in
                a + 1
            in x
            """);

        diag.Errors.ShouldContain(x => x.Contains("let? body must produce a Result(E, A) expression.", StringComparison.Ordinal));
    }

    [Test]
    public void Let_result_binding_preserves_typed_error_flow()
    {
        var (_, diag) = LowerProgram(
            """
            type AppError = | Fail(String)
            let x =
                let? a = Error(Fail("fail"))
                in
                Ok(a)
            in match x with
            | Ok(_) -> Ashes.IO.print("ok")
            | Error(Fail(msg)) -> Ashes.IO.print(msg)
            """);

        diag.Errors.ShouldBeEmpty();
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
