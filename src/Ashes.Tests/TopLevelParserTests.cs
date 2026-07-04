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
        var program = ParseProgram("type Foo = | Bar\nlet x = 1\nexternal foo() -> Int\n0");

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
    public void ParseProgram_should_treat_a_bare_trailing_call_after_a_flat_let_as_the_body()
    {
        // The trailing call must NOT be absorbed as a whitespace-application argument of `1`
        // (i.e. NOT `1 (print(a))`). It is a separate top-level item: the program's Body.
        var program = ParseProgram("let a = 1\nprint(a)");

        var letItem = program.Items.ShouldHaveSingleItem().ShouldBeOfType<TopLevelItem.LetDecl>();
        letItem.Name.ShouldBe("a");
        letItem.Value.ShouldBe(new Expr.IntLit(1));

        var call = program.Body.ShouldBeOfType<Expr.Call>();
        call.Func.ShouldBe(new Expr.Var("print"));
        call.Arg.ShouldBe(new Expr.Var("a"));
    }

    [Test]
    public void ParseProgram_should_treat_a_bare_trailing_identifier_after_a_flat_let_as_the_body()
    {
        var program = ParseProgram("let a = 1\na");

        program.Items.ShouldHaveSingleItem().ShouldBeOfType<TopLevelItem.LetDecl>().Name.ShouldBe("a");
        program.Body.ShouldBe(new Expr.Var("a"));
    }

    [Test]
    public void ParseProgram_should_treat_a_bare_trailing_if_after_a_flat_let_as_the_body()
    {
        var program = ParseProgram("let a = 1\nif a then 2 else 3");

        program.Items.ShouldHaveSingleItem().ShouldBeOfType<TopLevelItem.LetDecl>().Name.ShouldBe("a");
        program.Body.ShouldBeOfType<Expr.If>();
    }

    [Test]
    public void ParseProgram_should_treat_a_bare_trailing_qualified_call_after_a_flat_let_as_the_body()
    {
        var program = ParseProgram("let x = 5\nAshes.IO.print(x)");

        program.Items.ShouldHaveSingleItem().ShouldBeOfType<TopLevelItem.LetDecl>().Name.ShouldBe("x");
        var call = program.Body.ShouldBeOfType<Expr.Call>();
        call.Func.ShouldBeOfType<Expr.QualifiedVar>().Name.ShouldBe("print");
    }

    [Test]
    public void ParseProgram_should_not_absorb_the_next_decl_into_a_qualified_call_binding_value()
    {
        // A binding value that is a qualified application (`List.length(xs)`) must terminate at the
        // declaration boundary just like an unqualified one — the following decl/trailing expression
        // must NOT be pulled in as a whitespace-application argument.
        var program = ParseProgram("let xs = [1, 2, 3]\nlet ys = List.reverse(xs)\nlet n = List.length(ys)\nn");

        program.Items.Count.ShouldBe(3);
        program.Items[0].ShouldBeOfType<TopLevelItem.LetDecl>().Name.ShouldBe("xs");

        var ysItem = program.Items[1].ShouldBeOfType<TopLevelItem.LetDecl>();
        ysItem.Name.ShouldBe("ys");
        ysItem.Value.ShouldBeOfType<Expr.Call>().Func.ShouldBeOfType<Expr.QualifiedVar>().Name.ShouldBe("reverse");

        var nItem = program.Items[2].ShouldBeOfType<TopLevelItem.LetDecl>();
        nItem.Name.ShouldBe("n");
        nItem.Value.ShouldBeOfType<Expr.Call>().Func.ShouldBeOfType<Expr.QualifiedVar>().Name.ShouldBe("length");

        program.Body.ShouldBe(new Expr.Var("n"));
    }

    [Test]
    public void ParseProgram_should_parse_a_parenthesized_flat_declaration_block_as_nested_lets()
    {
        // The combined-source stitcher wraps a flat top-level entry as `(decl decl ... trailingExpr)`.
        // The parenthesized flat declarations (no `in`) must fold into nested let expressions instead
        // of being absorbed as whitespace-application arguments of the preceding value.
        var program = ParseProgram("(let xs = [1, 2, 3]\nlet n = List.length(xs)\nprint(n))");

        program.Items.ShouldBeEmpty();
        var outer = program.Body.ShouldBeOfType<Expr.Let>();
        outer.Name.ShouldBe("xs");

        var inner = outer.Body.ShouldBeOfType<Expr.Let>();
        inner.Name.ShouldBe("n");
        inner.Value.ShouldBeOfType<Expr.Call>().Func.ShouldBeOfType<Expr.QualifiedVar>().Name.ShouldBe("length");

        var call = inner.Body.ShouldBeOfType<Expr.Call>();
        call.Func.ShouldBe(new Expr.Var("print"));
    }

    [Test]
    public void ParseProgram_should_parse_the_stitched_flat_entry_form_with_qualified_call_values()
    {
        // Mirrors the exact shape the combined-source stitcher emits for a flat entry that imports a
        // flat standard-library module: a boundary binding whose `in` body is the parenthesized flat
        // declaration block.
        var program = ParseProgram("let m = 0 in (let xs = [1, 2, 3]\nlet n = List.length(xs)\nprint(n))");

        program.Items.ShouldBeEmpty();
        var boundary = program.Body.ShouldBeOfType<Expr.Let>();
        boundary.Name.ShouldBe("m");

        var xs = boundary.Body.ShouldBeOfType<Expr.Let>();
        xs.Name.ShouldBe("xs");
        var n = xs.Body.ShouldBeOfType<Expr.Let>();
        n.Name.ShouldBe("n");
        n.Value.ShouldBeOfType<Expr.Call>().Func.ShouldBeOfType<Expr.QualifiedVar>().Name.ShouldBe("length");
        n.Body.ShouldBeOfType<Expr.Call>().Func.ShouldBe(new Expr.Var("print"));
    }

    [Test]
    public void ParseProgram_should_still_parse_a_parenthesized_let_in_expression()
    {
        // The flat-block handling must not regress the ordinary parenthesized `let ... in` expression.
        var program = ParseProgram("(let x = 1 in x)");

        program.Items.ShouldBeEmpty();
        var letExpr = program.Body.ShouldBeOfType<Expr.Let>();
        letExpr.Name.ShouldBe("x");
        letExpr.Value.ShouldBe(new Expr.IntLit(1));
        letExpr.Body.ShouldBe(new Expr.Var("x"));
    }

    [Test]
    public void ParseProgram_should_terminate_a_flat_let_before_a_trailing_expression_between_two_decls()
    {
        var program = ParseProgram("let a = 1\nlet b = 2\nf(a)");

        program.Items.Count.ShouldBe(2);
        program.Items[0].ShouldBeOfType<TopLevelItem.LetDecl>().Name.ShouldBe("a");
        program.Items[1].ShouldBeOfType<TopLevelItem.LetDecl>().Name.ShouldBe("b");
        program.Body.ShouldBeOfType<Expr.Call>();
    }

    [Test]
    public void ParseProgram_should_preserve_indented_multiline_whitespace_application_in_a_flat_let_value()
    {
        // The continuation `y` is indented past the declaration's column, so it is a genuine
        // whitespace-application argument of `f` — NOT a new top-level item. Regressing this is
        // the failure mode the terminating rule must avoid.
        var program = ParseProgram("let x = f\n    y");

        var letItem = program.Items.ShouldHaveSingleItem().ShouldBeOfType<TopLevelItem.LetDecl>();
        letItem.Name.ShouldBe("x");
        var call = letItem.Value.ShouldBeOfType<Expr.Call>();
        call.Func.ShouldBe(new Expr.Var("f"));
        call.Arg.ShouldBe(new Expr.Var("y"));
        program.Body.ShouldBeNull();
    }

    [Test]
    public void ParseProgram_should_preserve_same_line_whitespace_application_in_a_flat_let_value()
    {
        var program = ParseProgram("let x = f y\ng");

        var letItem = program.Items.ShouldHaveSingleItem().ShouldBeOfType<TopLevelItem.LetDecl>();
        letItem.Value.ShouldBeOfType<Expr.Call>().Func.ShouldBe(new Expr.Var("f"));
        program.Body.ShouldBe(new Expr.Var("g"));
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
    public void ParseProgram_should_treat_a_complete_bare_let_in_value_followed_by_a_declaration_as_a_flat_declaration()
    {
        // `let f = let recursive go = ... in go` is a complete `let..in` expression; followed by another
        // top-level declaration it is a flat decl, not the nested pyramid (which needs an outer `in`).
        var diag = new Diagnostics();
        var program = new Parser(
            "let f = let recursive go = given (x) -> x in go\nlet g = 1",
            diag).ParseProgram();

        diag.Errors.ShouldBeEmpty();
        program.Items.Count.ShouldBe(2);

        var fItem = program.Items[0].ShouldBeOfType<TopLevelItem.LetDecl>();
        fItem.Name.ShouldBe("f");
        fItem.Value.ShouldBeOfType<Expr.LetRec>().Name.ShouldBe("go");

        var gItem = program.Items[1].ShouldBeOfType<TopLevelItem.LetDecl>();
        gItem.Name.ShouldBe("g");
        gItem.Value.ShouldBe(new Expr.IntLit(1));

        program.Body.ShouldBeNull();
    }

    [Test]
    public void ParseProgram_should_treat_a_complete_bare_let_in_value_followed_by_a_trailing_expr_as_a_flat_declaration()
    {
        var diag = new Diagnostics();
        var program = new Parser(
            "let f = let recursive go = given (x) -> x in go\nf(5)",
            diag).ParseProgram();

        diag.Errors.ShouldBeEmpty();

        var fItem = program.Items.ShouldHaveSingleItem().ShouldBeOfType<TopLevelItem.LetDecl>();
        fItem.Name.ShouldBe("f");
        fItem.Value.ShouldBeOfType<Expr.LetRec>().Name.ShouldBe("go");

        program.Body.ShouldNotBeNull();
    }

    [Test]
    public void ParseProgram_should_parse_single_let_rec_as_a_recursive_declaration()
    {
        var program = ParseProgram("let recursive loop x = loop x\n0");

        var letItem = program.Items.ShouldHaveSingleItem().ShouldBeOfType<TopLevelItem.LetDecl>();
        letItem.Name.ShouldBe("loop");
        letItem.IsRecursive.ShouldBeTrue();
    }

    [Test]
    public void ParseProgram_should_parse_let_rec_and_group_into_a_rec_group()
    {
        var program = ParseProgram("let recursive a = b\nand b = a");

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
