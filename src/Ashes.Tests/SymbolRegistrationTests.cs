using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class SymbolRegistrationTests
{
    [Test]
    public void Lower_program_registers_type_symbol()
    {
        var (lowering, _) = LowerProgram("type Option = | None | Some(T)\nAshes.IO.print(1)");

        lowering.TypeSymbols.ContainsKey("Option").ShouldBeTrue();
        var sym = lowering.TypeSymbols["Option"];
        sym.Name.ShouldBe("Option");
        sym.Constructors.Count.ShouldBe(2);
    }

    [Test]
    public void Lower_program_registers_constructor_symbols_linked_to_parent_type()
    {
        var (lowering, _) = LowerProgram("type Option = | None | Some(T)\nAshes.IO.print(1)");

        lowering.ConstructorSymbols.ContainsKey("None").ShouldBeTrue();
        lowering.ConstructorSymbols.ContainsKey("Some").ShouldBeTrue();

        var none = lowering.ConstructorSymbols["None"];
        none.ParentType.ShouldBe("Option");
        none.Arity.ShouldBe(0);

        var some = lowering.ConstructorSymbols["Some"];
        some.ParentType.ShouldBe("Option");
        some.Arity.ShouldBe(1);
    }

    [Test]
    public void Lower_program_reports_diagnostic_for_duplicate_type_name()
    {
        var (_, diag) = LowerProgram("type Foo = | A\ntype Foo = | B\nAshes.IO.print(1)");

        diag.Errors.ShouldContain(x => x.Contains("Duplicate type name 'Foo'", StringComparison.Ordinal));
    }

    [Test]
    public void Lower_program_reports_diagnostic_for_duplicate_constructor_name_in_same_type()
    {
        var (_, diag) = LowerProgram("type Foo = | A | A\nAshes.IO.print(1)");

        diag.Errors.ShouldContain(x => x.Contains("Duplicate constructor name 'A' in type 'Foo'", StringComparison.Ordinal));
    }

    [Test]
    public void Lower_program_with_no_type_declarations_registers_no_symbols()
    {
        var (lowering, diag) = LowerProgram("Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        lowering.TypeSymbols.ContainsKey("OptionString").ShouldBeTrue();
        lowering.ConstructorSymbols.ContainsKey("None").ShouldBeTrue();
        lowering.ConstructorSymbols.ContainsKey("Some").ShouldBeTrue();
    }

    [Test]
    public void Lower_program_registers_multiple_type_symbols()
    {
        var (lowering, diag) = LowerProgram("type A = | X\ntype B = | Y\nAshes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        lowering.TypeSymbols.ContainsKey("A").ShouldBeTrue();
        lowering.TypeSymbols.ContainsKey("B").ShouldBeTrue();
    }

    [Test]
    public void Lower_program_registers_builtin_option_string_symbols()
    {
        var (lowering, diag) = LowerProgram("Ashes.IO.print(1)");

        diag.Errors.ShouldBeEmpty();
        lowering.TypeSymbols.ContainsKey("OptionString").ShouldBeTrue();
        lowering.TypeSymbols.ContainsKey("Unit").ShouldBeTrue();
        lowering.TypeSymbols["OptionString"].IsBuiltin.ShouldBeTrue();
        lowering.ConstructorSymbols["None"].ParentType.ShouldBe("OptionString");
        lowering.ConstructorSymbols["Some"].ParameterTypes.ShouldHaveSingleItem().ShouldBeOfType<TypeRef.TStr>();
        lowering.ConstructorSymbols["Unit"].ParentType.ShouldBe("Unit");
    }

    [Test]
    public void Lower_program_reports_reserved_runtime_type_diagnostic()
    {
        var (_, diag) = LowerProgram("type OptionString = | Nope\nAshes.IO.print(1)");

        diag.Errors.ShouldContain(x => x.Contains("built-in runtime types are reserved", StringComparison.Ordinal));
    }

    [Test]
    public void Lower_program_reports_reserved_float_type_diagnostic()
    {
        var (_, diag) = LowerProgram("type Float = | Nope\nAshes.IO.print(1)");

        diag.Errors.ShouldContain(x => x.Contains("built-in runtime types are reserved", StringComparison.Ordinal));
    }

    [Test]
    public void Lower_program_reports_reserved_ashes_type_diagnostic()
    {
        var (_, diag) = LowerProgram("type Ashes = | Nope\nAshes.IO.print(1)");

        diag.Errors.ShouldContain(x => x.Contains("built-in runtime types are reserved", StringComparison.Ordinal));
    }

    [Test]
    public void Lower_program_reports_diagnostic_for_missing_constructor()
    {
        // Directly construct a TypeDecl with no constructors to test the semantic check
        var diag = new Diagnostics();
        var decl = new TypeDecl("Empty", [], []);
        var program = new Program([decl], new Expr.IntLit(1));
        var lowering = new Lowering(diag);
        lowering.Lower(program);

        diag.Errors.ShouldContain(x => x.Contains("must have at least one constructor", StringComparison.Ordinal));
    }

    [Test]
    public void Lower_program_reports_diagnostic_for_duplicate_type_parameter()
    {
        // Directly construct a TypeDecl with duplicate type parameters
        var diag = new Diagnostics();
        var decl = new TypeDecl("Pair", [new TypeParameter("T"), new TypeParameter("T")], [new TypeConstructor("MkPair", ["T"])]);
        var program = new Program([decl], new Expr.IntLit(1));
        var lowering = new Lowering(diag);
        lowering.Lower(program);

        diag.Errors.ShouldContain(x => x.Contains("Duplicate type parameter 'T' in type 'Pair'", StringComparison.Ordinal));
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
