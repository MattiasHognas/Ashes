using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class TopLevelAstTests
{
    [Test]
    public void Program_exposes_items_in_source_order()
    {
        var typeA = new TypeDecl("A", [], [new TypeConstructor("MkA", [])]);
        var letX = new TopLevelItem.LetDecl("x", new Expr.IntLit(1), IsRecursive: false);
        var ext = new ExternalDecl.Function("ext", [], new ParsedType.Named("Int"));
        var typeB = new TypeDecl("B", [], [new TypeConstructor("MkB", [])]);
        var recursiveGroup = new TopLevelItem.RecursiveGroup(
        [
            ("f", new Expr.IntLit(2)),
            ("g", new Expr.IntLit(3)),
        ]);
        var letY = new TopLevelItem.LetDecl("y", new Expr.IntLit(4), IsRecursive: true);

        TopLevelItem[] items =
        [
            new TopLevelItem.Type(typeA),
            letX,
            new TopLevelItem.External(ext),
            new TopLevelItem.Type(typeB),
            recursiveGroup,
            letY,
        ];

        var program = new Program(items, Body: null);

        program.Items.Count.ShouldBe(6);
        program.Items.ShouldBe(items);
    }

    [Test]
    public void TypeDecls_accessor_returns_type_items_in_order()
    {
        var typeA = new TypeDecl("A", [], [new TypeConstructor("MkA", [])]);
        var typeB = new TypeDecl("B", [], [new TypeConstructor("MkB", [])]);

        TopLevelItem[] items =
        [
            new TopLevelItem.Type(typeA),
            new TopLevelItem.LetDecl("x", new Expr.IntLit(1), IsRecursive: false),
            new TopLevelItem.External(new ExternalDecl.Function("ext", [], new ParsedType.Named("Int"))),
            new TopLevelItem.Type(typeB),
        ];

        var program = new Program(items, Body: null);

        var typeDecls = program.TypeDecls;
        typeDecls.Count.ShouldBe(2);
        typeDecls[0].ShouldBeSameAs(typeA);
        typeDecls[1].ShouldBeSameAs(typeB);
    }

    [Test]
    public void ExternalDecls_accessor_returns_external_items_in_order()
    {
        var ext1 = new ExternalDecl.Function("first", [], new ParsedType.Named("Int"));
        var ext2 = new ExternalDecl.OpaqueType("Handle");

        TopLevelItem[] items =
        [
            new TopLevelItem.External(ext1),
            new TopLevelItem.Type(new TypeDecl("A", [], [new TypeConstructor("MkA", [])])),
            new TopLevelItem.LetDecl("x", new Expr.IntLit(1), IsRecursive: false),
            new TopLevelItem.External(ext2),
        ];

        var program = new Program(items, Body: null);

        var externalDecls = program.ExternalDecls;
        externalDecls.Count.ShouldBe(2);
        externalDecls[0].ShouldBeSameAs(ext1);
        externalDecls[1].ShouldBeSameAs(ext2);
    }

    [Test]
    public void Program_supports_a_missing_trailing_expression()
    {
        var program = new Program([new TopLevelItem.LetDecl("x", new Expr.IntLit(1), IsRecursive: false)], Body: null);

        program.Body.ShouldBeNull();
    }

    [Test]
    public void RecursiveGroup_preserves_binding_order()
    {
        var first = new Expr.IntLit(1);
        var second = new Expr.IntLit(2);
        var recursiveGroup = new TopLevelItem.RecursiveGroup(
        [
            ("even", first),
            ("odd", second),
        ]);

        recursiveGroup.Bindings.Count.ShouldBe(2);
        recursiveGroup.Bindings[0].Name.ShouldBe("even");
        recursiveGroup.Bindings[0].Value.ShouldBeSameAs(first);
        recursiveGroup.Bindings[1].Name.ShouldBe("odd");
        recursiveGroup.Bindings[1].Value.ShouldBeSameAs(second);
    }

    [Test]
    public void Back_compat_constructor_orders_types_then_externals_with_body()
    {
        var typeA = new TypeDecl("A", [], [new TypeConstructor("MkA", [])]);
        var typeB = new TypeDecl("B", [], [new TypeConstructor("MkB", [])]);
        var ext = new ExternalDecl.Function("ext", [], new ParsedType.Named("Int"));
        var body = new Expr.IntLit(42);

        var program = new Program([typeA, typeB], [ext], body);

        program.TypeDecls.Count.ShouldBe(2);
        program.TypeDecls[0].ShouldBeSameAs(typeA);
        program.TypeDecls[1].ShouldBeSameAs(typeB);
        program.ExternalDecls.Count.ShouldBe(1);
        program.ExternalDecls[0].ShouldBeSameAs(ext);
        program.Body.ShouldBeSameAs(body);

        // Items preserve the "types first, then externals" ordering of the legacy constructor.
        program.Items.Count.ShouldBe(3);
        program.Items[0].ShouldBeOfType<TopLevelItem.Type>().Decl.ShouldBeSameAs(typeA);
        program.Items[1].ShouldBeOfType<TopLevelItem.Type>().Decl.ShouldBeSameAs(typeB);
        program.Items[2].ShouldBeOfType<TopLevelItem.External>().Decl.ShouldBeSameAs(ext);
    }
}
