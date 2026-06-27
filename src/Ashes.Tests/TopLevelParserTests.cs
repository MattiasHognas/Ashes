using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class TopLevelParserTests
{
    [Test]
    public void ParseProgram_should_parse_a_lone_trailing_expression()
    {
        var program = ParseProgram("42");

        program.Items.ShouldBeEmpty();
        program.Body.ShouldBe(new Expr.IntLit(42));
    }

    [Test]
    public void ParseProgram_should_parse_interleaved_type_let_and_extern_declarations_in_order()
    {
        var program = ParseProgram("type Foo = | Bar\nlet x = 1\nextern foo() -> Int\n0");

        program.Items.Count.ShouldBe(3);

        var typeItem = program.Items[0].ShouldBeOfType<TopLevelItem.Type>();
        typeItem.Decl.Name.ShouldBe("Foo");

        var letItem = program.Items[1].ShouldBeOfType<TopLevelItem.LetDecl>();
        letItem.Name.ShouldBe("x");
        letItem.IsRecursive.ShouldBeFalse();
        letItem.Value.ShouldBe(new Expr.IntLit(1));

        var externItem = program.Items[2].ShouldBeOfType<TopLevelItem.Extern>();
        externItem.Decl.ShouldBeOfType<ExternDecl.Function>().Name.ShouldBe("foo");

        program.Body.ShouldBe(new Expr.IntLit(0));
    }

    [Test]
    public void ParseProgram_should_treat_consecutive_flat_lets_without_in_as_top_level_declarations()
    {
        var program = ParseProgram("let x = 1\nlet y = 2");

        program.Items.Count.ShouldBe(2);

        var first = program.Items[0].ShouldBeOfType<TopLevelItem.LetDecl>();
        first.Name.ShouldBe("x");
        first.IsRecursive.ShouldBeFalse();
        first.Value.ShouldBe(new Expr.IntLit(1));

        var second = program.Items[1].ShouldBeOfType<TopLevelItem.LetDecl>();
        second.Name.ShouldBe("y");
        second.Value.ShouldBe(new Expr.IntLit(2));

        program.Body.ShouldBeNull();
    }

    [Test]
    public void ParseProgram_should_treat_let_with_in_as_a_nested_let_expression()
    {
        var program = ParseProgram("let x = 1 in x");

        program.Items.ShouldBeEmpty();
        var letExpr = program.Body.ShouldBeOfType<Expr.Let>();
        letExpr.Name.ShouldBe("x");
        letExpr.Value.ShouldBe(new Expr.IntLit(1));
        letExpr.Body.ShouldBe(new Expr.Var("x"));
    }

    [Test]
    public void ParseProgram_should_treat_a_let_with_a_bare_let_in_value_as_a_nested_let_expression()
    {
        var program = ParseProgram("let x = let y = 2 in y in x + 1");

        program.Items.ShouldBeEmpty();
        var outer = program.Body.ShouldBeOfType<Expr.Let>();
        outer.Name.ShouldBe("x");
        outer.Value.ShouldBeOfType<Expr.Let>().Name.ShouldBe("y");
    }

    [Test]
    public void ParseProgram_should_require_an_outer_in_for_a_bare_let_in_value()
    {
        // Without the outer `in` this is ambiguous with the nested-let pyramid, so it is reported
        // as a missing `in` rather than silently parsed as a flat declaration. (The REPL relies on
        // this diagnostic to keep reading the continuation line.)
        var diag = new Diagnostics();
        _ = new Parser("let x = let y = 2 in y", diag).ParseProgram();
        diag.Errors.ShouldNotBeEmpty();
    }

    [Test]
    public void ParseProgram_should_treat_a_parenthesized_let_in_value_as_a_flat_declaration()
    {
        var program = ParseProgram("let x = (let y = 2 in y)");

        var letItem = program.Items.ShouldHaveSingleItem().ShouldBeOfType<TopLevelItem.LetDecl>();
        letItem.Name.ShouldBe("x");
        letItem.Value.ShouldBeOfType<Expr.Let>().Name.ShouldBe("y");
        program.Body.ShouldBeNull();
    }

    [Test]
    public void ParseProgram_should_parse_single_let_rec_as_a_recursive_declaration()
    {
        var program = ParseProgram("let rec loop x = loop x\n0");

        var letItem = program.Items.ShouldHaveSingleItem().ShouldBeOfType<TopLevelItem.LetDecl>();
        letItem.Name.ShouldBe("loop");
        letItem.IsRecursive.ShouldBeTrue();
    }

    [Test]
    public void ParseProgram_should_parse_let_rec_and_group_into_a_rec_group()
    {
        var program = ParseProgram("let rec a = b\nand b = a");

        var group = program.Items.ShouldHaveSingleItem().ShouldBeOfType<TopLevelItem.RecGroup>();
        group.Bindings.Count.ShouldBe(2);
        group.Bindings[0].Name.ShouldBe("a");
        group.Bindings[0].Value.ShouldBe(new Expr.Var("b"));
        group.Bindings[1].Name.ShouldBe("b");
        group.Bindings[1].Value.ShouldBe(new Expr.Var("a"));

        program.Body.ShouldBeNull();
    }

    [Test]
    public void ParseProgram_should_allow_a_file_that_ends_after_the_last_declaration()
    {
        var program = ParseProgram("let x = 1");

        program.Items.ShouldHaveSingleItem().ShouldBeOfType<TopLevelItem.LetDecl>().Name.ShouldBe("x");
        program.Body.ShouldBeNull();
    }

    [Test]
    public void ParseProgram_should_allow_multiple_declarations_with_no_trailing_expression()
    {
        var program = ParseProgram("type Foo = | Bar\nlet x = 1");

        program.Items.Count.ShouldBe(2);
        program.Items[0].ShouldBeOfType<TopLevelItem.Type>();
        program.Items[1].ShouldBeOfType<TopLevelItem.LetDecl>();
        program.Body.ShouldBeNull();
    }

    [Test]
    public void ParseProgram_should_reject_and_that_is_not_part_of_a_let_rec_group()
    {
        var diag = new Diagnostics();
        _ = new Parser("let x = 1 and y = 2", diag).ParseProgram();
        diag.Errors.ShouldNotBeEmpty();
    }

    [Test]
    public void ParseProgram_should_reject_a_bare_and()
    {
        var diag = new Diagnostics();
        _ = new Parser("and x = 1", diag).ParseProgram();
        diag.Errors.ShouldNotBeEmpty();
    }

    private static Program ParseProgram(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.Errors.ShouldBeEmpty();
        return program;
    }
}
