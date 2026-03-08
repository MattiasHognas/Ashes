using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;
using System.Reflection;

namespace Ashes.Tests;

public sealed class TypePrettyPrintingTests
{
    [Test]
    public void Type_mismatch_pretty_prints_function_type_variables_with_stable_names()
    {
        var (_, diag) = LowerProgram("if true then fun (x) -> x else 1");

        diag.Errors.ShouldContain(x => x.Contains("Type mismatch: a -> a vs Int.", StringComparison.Ordinal));
        diag.Errors.ShouldNotContain(x => x.Contains("t0", StringComparison.Ordinal) || x.Contains("t1", StringComparison.Ordinal));
    }

    [Test]
    public void Binary_operator_diagnostic_uses_consistent_type_variable_names_across_both_sides()
    {
        var (_, diag) = LowerProgram("(fun (x) -> x) + (fun (y) -> y)");

        diag.Errors.ShouldContain(x => x.Contains("'+' requires Int+Int, Float+Float, or Str+Str, got a -> a and b -> b.", StringComparison.Ordinal));
    }

    [Test]
    public void Type_mismatch_parenthesizes_function_type_inside_list_type_argument()
    {
        var (_, diag) = LowerProgram("if true then [fun (x) -> x] else 1");

        diag.Errors.ShouldContain(x => x.Contains("Type mismatch: List<(a -> a)> vs Int.", StringComparison.Ordinal));
    }

    [Test]
    public void Pretty_parenthesizes_function_type_inside_named_type_argument()
    {
        var lowering = new Lowering(new Diagnostics());
        var prettyMethod = typeof(Lowering)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == "Pretty" && m.GetParameters().Length == 1);

        var typeSymbol = new TypeSymbol(
            "Option",
            [new TypeParameterSymbol("T")],
            [],
            new TypeDecl("Option", [new TypeParameter("T")], [new TypeConstructor("None", [])]));
        var namedType = new TypeRef.TNamedType(typeSymbol, [new TypeRef.TFun(new TypeRef.TVar(0), new TypeRef.TVar(0))]);

        var rendered = (string)prettyMethod.Invoke(lowering, [namedType])!;
        rendered.ShouldBe("Option<(a -> a)>");
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
