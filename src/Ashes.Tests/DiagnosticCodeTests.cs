using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class DiagnosticCodeTests
{
    [Test]
    public void Unknown_identifier_should_emit_ash001()
    {
        var diag = LowerExpression("missing");

        diag.StructuredErrors.ShouldContain(x => x.Code == DiagnosticCodes.UnknownIdentifier);
    }

    [Test]
    public void Type_mismatch_should_emit_ash002()
    {
        var diag = LowerExpression("if true then 1 else \"a\"");

        diag.StructuredErrors.ShouldContain(x => x.Code == DiagnosticCodes.TypeMismatch);
    }

    [Test]
    public void Parse_error_should_emit_ash003()
    {
        var diag = new Diagnostics();
        _ = new Parser("if true then 1", diag).ParseProgram();

        diag.StructuredErrors.ShouldContain(x => x.Code == DiagnosticCodes.ParseError);
    }

    [Test]
    public void Match_branch_type_mismatch_should_emit_ash004()
    {
        var diag = LowerExpression(
            """
            let x =
                match true with
                    | true -> 1
                    | false -> "bad"
            in x
            """);

        diag.StructuredErrors.ShouldContain(x => x.Code == DiagnosticCodes.MatchBranchTypeMismatch);
    }

    [Test]
    public void List_element_type_mismatch_should_emit_ash005()
    {
        var diag = LowerExpression("[1, \"a\"]");

        diag.StructuredErrors.ShouldContain(x => x.Code == DiagnosticCodes.ListElementTypeMismatch);
    }

    [Test]
    public void Await_outside_async_should_emit_ash010()
    {
        var diag = LowerExpression("await 42");

        diag.StructuredErrors.ShouldContain(x => x.Code == DiagnosticCodes.AwaitOutsideAsync);
    }

    [Test]
    public void Await_inside_async_should_not_emit_ash010()
    {
        var diag = LowerExpression("async await (async 42)");

        diag.StructuredErrors.ShouldNotContain(x => x.Code == DiagnosticCodes.AwaitOutsideAsync);
    }

    [Test]
    public void Async_only_networking_api_should_emit_ash012_outside_async()
    {
        var diag = LowerExpression("Ashes.Http.get(\"http://example.com\")");

        diag.StructuredErrors.ShouldContain(x => x.Code == DiagnosticCodes.AsyncOnlyNetworkingApi);
    }

    private static Diagnostics LowerExpression(string source)
    {
        var diag = new Diagnostics();
        var expr = new Parser(source, diag).ParseExpression();
        _ = new Lowering(diag).Lower(expr);
        return diag;
    }
}
