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

    // `await` and the networking builtins are intentionally usable outside any `async` context:
    // `async` is a builtin (Ashes.Async.task), not a block keyword, so there is nothing to be
    // "outside" of. `await` runs its task wherever it appears, and async-only safety is enforced by
    // the `Task` type. These guards lock in that permissive behaviour so the abandoned
    // await-/networking-outside-async enforcement does not silently reappear.

    [Test]
    public void Await_outside_async_compiles_without_error()
    {
        var diag = LowerExpression("await Ashes.Async.task(42)");

        diag.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void Networking_builtin_outside_async_compiles_without_error()
    {
        LowerExpression("Ashes.Http.get(\"http://example.com\")").StructuredErrors.ShouldBeEmpty();
        LowerExpression("Ashes.Http.get(\"https://example.com\")").StructuredErrors.ShouldBeEmpty();
    }

    private static Diagnostics LowerExpression(string source)
    {
        var diag = new Diagnostics();
        var expr = new Parser(source, diag).ParseExpression();
        _ = new Lowering(diag).Lower(expr);
        return diag;
    }
}
