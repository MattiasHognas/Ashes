using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class TraitDefaultMethodTests
{
    [Test]
    public void UnusedDefaultMethodIsTypeCheckedAtItsDeclaration()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool = given (left) -> given (right) -> 1
            0
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.Select(error => error.Message)
            .ShouldContain(message => message.Contains("Bool", StringComparison.Ordinal));
    }

    [Test]
    public void UnusedImplementationMethodIsTypeCheckedAtItsDeclaration()
    {
        const string source = """
            trait Eq(a) = | equal : a -> a -> Bool
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> "not a Bool"
            0
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.Select(error => error.Message)
            .ShouldContain(message => message.Contains("Bool", StringComparison.Ordinal));
    }

    [Test]
    public void DefaultOnlyDependencyCycleIsRejectedBeforeUse()
    {
        const string source = """
            trait Choice(a) =
                | first : a -> Bool = given (value) -> Choice.second(value)
                | second : a -> Bool = given (value) -> Choice.first(value)
                | base : a -> Bool
            implement Choice(Int) =
                | base = given (value) -> true
            0
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.Select(error => error.Message)
            .ShouldContain(message => message.Contains("dependency cycle", StringComparison.Ordinal));
    }

    [Test]
    public void OverrideBreaksAChainOfDefaults()
    {
        const string source = """
            trait Choice(a) =
                | first : a -> Bool = given (value) -> Choice.second(value)
                | second : a -> Bool = given (value) -> Choice.first(value)
            implement Choice(Int) =
                | first = given (value) -> true
            Choice.second(1)
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void DiamondSupertraitsShareCanonicalEvidenceWithoutAmbiguity()
    {
        const string source = """
            trait Root(a) = | root : a -> Bool
            trait Left(a) requires {Root(a)} = | left : a -> Bool
            trait Right(a) requires {Root(a)} = | right : a -> Bool
            trait Diamond(a) requires {Left(a), Right(a)} = | diamond : a -> Bool
            implement Root(Int) = | root = given (value) -> true
            implement Left(Int) = | left = given (value) -> Root.root(value)
            implement Right(Int) = | right = given (value) -> Root.root(value)
            implement Diamond(Int) = | diamond = given (value) -> Left.left(value)
            let check : a -> Bool requires {Diamond(a)} =
                given (value) ->
                    if Root.root(value) then Diamond.diamond(value) else false
            check(1)
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void DefaultMethodIsCheckedUnderItsDeclaredCapabilityRow()
    {
        const string source = """
            capability Log = | emit : Str -> Unit
            trait Audit(a) =
                | audit : a -> Unit needs {Log} =
                    given (value) -> perform Log.emit("audit")
                | marker : a -> Bool
            implement Audit(Int) = | marker = given (value) -> true
            0
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void DefaultMethodCannotPerformAnUndeclaredCapability()
    {
        const string source = """
            capability Log = | emit : Str -> Unit
            trait Audit(a) =
                | audit : a -> Unit =
                    given (value) -> perform Log.emit("audit")
            0
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.Select(error => error.Message)
            .ShouldContain(message => message.Contains("not permitted", StringComparison.Ordinal));
    }

    private static IrProgram Lower(string source, out Diagnostics diagnostics)
    {
        diagnostics = new Diagnostics();
        Ashes.Frontend.Program syntax = new Parser(source, diagnostics).ParseProgram();
        return new Lowering(diagnostics).Lower(syntax);
    }
}
