using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class TypeResolutionTests
{
    [Test]
    public void Resolved_types_contains_named_type_for_declared_type()
    {
        var (lowering, diag) = LowerProgram("type Bool = | True | False\nAshes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        lowering.ResolvedTypes.ContainsKey("Bool").ShouldBeTrue();
        var named = lowering.ResolvedTypes["Bool"];
        named.Symbol.Name.ShouldBe("Bool");
        named.TypeArgs.ShouldBeEmpty();
    }

    [Test]
    public void Resolve_type_name_returns_named_type_for_known_type()
    {
        var (lowering, diag) = LowerProgram("type Option = | None | Some(T)\nAshes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        var resolved = lowering.ResolveTypeName("Option", [new TypeRef.TInt()]);
        resolved.ShouldBeOfType<TypeRef.TNamedType>();
        var named = (TypeRef.TNamedType)resolved;
        named.Symbol.Name.ShouldBe("Option");
    }

    [Test]
    public void Builtin_result_type_is_registered_with_expected_type_parameters()
    {
        var (lowering, diag) = LowerProgram("Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        var named = lowering.ResolvedTypes["Result"];
        named.TypeArgs.Count.ShouldBe(0);
        named.Symbol.TypeParameters.Select(x => x.Name).ShouldBe(["E", "A"]);
    }

    [Test]
    public void Implicit_constructor_parameters_still_register_as_type_parameters()
    {
        var (lowering, diag) = LowerProgram("type Option = | None | Some(T)\nAshes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        lowering.TypeSymbols["Option"].TypeParameters.Select(x => x.Name).ShouldBe(["T"]);
    }

    [Test]
    public void Resolve_type_name_reports_diagnostic_for_unknown_type()
    {
        var (lowering, diag) = LowerProgram("Ashes.IO.print(1)");

        lowering.ResolveTypeName("Unknown");
        diag.Errors.ShouldContain(x => x.Contains("Unknown type name 'Unknown'", StringComparison.Ordinal));
    }

    [Test]
    public void Resolve_type_name_reports_diagnostic_for_wrong_arity()
    {
        var (lowering, diag) = LowerProgram("type Bool = | True | False\nAshes.IO.print(1)");

        lowering.ResolveTypeName("Bool", [new TypeRef.TInt()]);
        diag.Errors.ShouldContain(x => x.Contains("Type 'Bool' expects 0 type argument(s) but got 1", StringComparison.Ordinal));
    }

    [Test]
    public void Named_type_participates_in_equality()
    {
        var (lowering, _) = LowerProgram("type Bool = | True | False\nAshes.IO.print(1)");

        var t1 = lowering.ResolvedTypes["Bool"];
        var t2 = lowering.ResolvedTypes["Bool"];
        t1.ShouldBe(t2);
    }

    [Test]
    public void Type_with_constructor_parameters_can_be_resolved()
    {
        var (lowering, diag) = LowerProgram("type Pair = | MkPair(A)\nAshes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        lowering.ResolvedTypes.ContainsKey("Pair").ShouldBeTrue();
    }

    [Test]
    public void Builtin_option_string_type_can_be_resolved()
    {
        var (lowering, diag) = LowerProgram("Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        var resolved = lowering.ResolveTypeName("OptionString");
        resolved.ShouldBeOfType<TypeRef.TNamedType>();
        ((TypeRef.TNamedType)resolved).Symbol.IsBuiltin.ShouldBeTrue();
    }

    [Test]
    public void Builtin_unit_type_can_be_resolved()
    {
        var (lowering, diag) = LowerProgram("Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        var resolved = lowering.ResolveTypeName("Unit");
        resolved.ShouldBeOfType<TypeRef.TNamedType>();
        ((TypeRef.TNamedType)resolved).Symbol.Name.ShouldBe("Unit");
    }

    [Test]
    public void Builtin_socket_type_can_be_resolved()
    {
        var (lowering, diag) = LowerProgram("Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        var resolved = lowering.ResolveTypeName("Socket");
        resolved.ShouldBeOfType<TypeRef.TNamedType>();
        ((TypeRef.TNamedType)resolved).Symbol.Name.ShouldBe("Socket");
        ((TypeRef.TNamedType)resolved).Symbol.Constructors.ShouldBeEmpty();
    }

    [Test]
    public void Builtin_float_type_can_be_resolved()
    {
        var (lowering, diag) = LowerProgram("Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        lowering.ResolveTypeName("Float").ShouldBeOfType<TypeRef.TFloat>();
    }

    [Test]
    public void Format_type_renders_float()
    {
        var (lowering, diag) = LowerProgram("Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        lowering.FormatType(new TypeRef.TFloat()).ShouldBe("Float");
    }

    [Test]
    public void Float_literal_is_lowered_with_float_type()
    {
        var diag = new Diagnostics();
        var expr = new Parser("1.5", diag).ParseExpression();
        var lowering = new Lowering(diag);

        lowering.Lower(expr);

        diag.Errors.ShouldBeEmpty();
        lowering.GetTypeAtPosition(1).ShouldNotBeNull();
        lowering.GetTypeAtPosition(1)!.Value.Type.ShouldBeOfType<TypeRef.TFloat>();
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
