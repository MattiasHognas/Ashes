using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class ParserEdgeCaseTests
{
    [Test]
    public void Parse_should_support_constructor_pattern_in_match()
    {
        var match = Parse("match x with | Some(y) -> y | None -> 0")
            .ShouldBeOfType<Expr.Match>();

        match.Cases.Count.ShouldBe(2);
        var ctorPat = match.Cases[0].Pattern.ShouldBeOfType<Pattern.Constructor>();
        ctorPat.Name.ShouldBe("Some");
        ctorPat.Patterns.Count.ShouldBe(1);
        ctorPat.Patterns[0].ShouldBeOfType<Pattern.Var>().Name.ShouldBe("y");
    }

    [Test]
    public void Parse_should_support_wildcard_pattern()
    {
        var match = Parse("match x with | _ -> 42")
            .ShouldBeOfType<Expr.Match>();

        match.Cases.Count.ShouldBe(1);
        match.Cases[0].Pattern.ShouldBeOfType<Pattern.Wildcard>();
        match.Cases[0].Body.ShouldBe(new Expr.IntLit(42));
    }

    [Test]
    public void Parse_should_support_nested_constructor_patterns()
    {
        var match = Parse("match x with | Ok(Some(v)) -> v | _ -> 0")
            .ShouldBeOfType<Expr.Match>();

        var outer = match.Cases[0].Pattern.ShouldBeOfType<Pattern.Constructor>();
        outer.Name.ShouldBe("Ok");
        var inner = outer.Patterns[0].ShouldBeOfType<Pattern.Constructor>();
        inner.Name.ShouldBe("Some");
        inner.Patterns[0].ShouldBeOfType<Pattern.Var>().Name.ShouldBe("v");
    }

    [Test]
    public void Parse_should_support_match_with_single_case()
    {
        var match = Parse("match x with | y -> true")
            .ShouldBeOfType<Expr.Match>();

        match.Cases.Count.ShouldBe(1);
    }

    [Test]
    public void Parse_should_support_deeply_nested_let_expressions()
    {
        var expr = Parse("let a = 1 in let b = 2 in let c = 3 in a");

        var outerLet = expr.ShouldBeOfType<Expr.Let>();
        outerLet.Name.ShouldBe("a");
        var middleLet = outerLet.Body.ShouldBeOfType<Expr.Let>();
        middleLet.Name.ShouldBe("b");
        var innerLet = middleLet.Body.ShouldBeOfType<Expr.Let>();
        innerLet.Name.ShouldBe("c");
    }

    [Test]
    public void Parse_should_support_pipe_operator_as_reversed_call()
    {
        // |> desugars to Expr.Call(right, left) — reversed call
        var expr = Parse("1 |> f").ShouldBeOfType<Expr.Call>();

        expr.Func.ShouldBe(new Expr.Var("f"));
        expr.Arg.ShouldBe(new Expr.IntLit(1));
    }

    [Test]
    public void Parse_should_support_qualified_variable_with_two_segments()
    {
        var expr = Parse("Ashes.IO.print").ShouldBeOfType<Expr.QualifiedVar>();

        expr.Module.ShouldBe("Ashes.IO");
        expr.Name.ShouldBe("print");
    }

    [Test]
    public void Parse_should_support_boolean_literals()
    {
        Parse("true").ShouldBe(new Expr.BoolLit(true));
        Parse("false").ShouldBe(new Expr.BoolLit(false));
    }

    [Test]
    public void Parse_should_support_string_literal()
    {
        Parse("\"hello\"").ShouldBe(new Expr.StrLit("hello"));
    }

    [Test]
    public void Parse_should_support_if_then_else()
    {
        var expr = Parse("if true then 1 else 2").ShouldBeOfType<Expr.If>();

        expr.Cond.ShouldBe(new Expr.BoolLit(true));
        expr.Then.ShouldBe(new Expr.IntLit(1));
        expr.Else.ShouldBe(new Expr.IntLit(2));
    }

    [Test]
    public void Parse_should_support_lambda_with_multiple_parameters()
    {
        var expr = Parse("fun (a, b) -> a");

        // Multi-param lambda desugars to nested lambdas
        var outer = expr.ShouldBeOfType<Expr.Lambda>();
        outer.ParamName.ShouldBe("a");
        var inner = outer.Body.ShouldBeOfType<Expr.Lambda>();
        inner.ParamName.ShouldBe("b");
    }

    [Test]
    public void Parse_should_support_single_parameter_lambda()
    {
        var expr = Parse("fun (x) -> x").ShouldBeOfType<Expr.Lambda>();

        expr.ParamName.ShouldBe("x");
        expr.Body.ShouldBe(new Expr.Var("x"));
    }

    [Test]
    public void Parse_should_support_result_pipe_operators()
    {
        var expr = Parse("x |?> f").ShouldBeOfType<Expr.ResultPipe>();
        expr.Left.ShouldBe(new Expr.Var("x"));
        expr.Right.ShouldBe(new Expr.Var("f"));
    }

    [Test]
    public void Parse_should_support_error_pipe_operator()
    {
        var expr = Parse("x |!> f").ShouldBeOfType<Expr.ResultMapErrorPipe>();
        expr.Left.ShouldBe(new Expr.Var("x"));
        expr.Right.ShouldBe(new Expr.Var("f"));
    }

    [Test]
    public void Parse_should_support_comparison_operators()
    {
        Parse("1 == 2").ShouldBeOfType<Expr.Equal>();
        Parse("1 != 2").ShouldBeOfType<Expr.NotEqual>();
        Parse("1 >= 2").ShouldBeOfType<Expr.GreaterOrEqual>();
        Parse("1 <= 2").ShouldBeOfType<Expr.LessOrEqual>();
    }

    [Test]
    public void Parse_should_support_cons_with_list_literal()
    {
        var cons = Parse("1 :: []").ShouldBeOfType<Expr.Cons>();

        cons.Head.ShouldBe(new Expr.IntLit(1));
        cons.Tail.ShouldBeOfType<Expr.ListLit>().Elements.ShouldBeEmpty();
    }

    [Test]
    public void Parse_should_support_let_rec()
    {
        var expr = Parse("let rec f = fun (x) -> f(x) in f(1)");

        var letRec = expr.ShouldBeOfType<Expr.LetRec>();
        letRec.Name.ShouldBe("f");
    }

    [Test]
    public void Parse_should_support_let_sugar_with_parameters()
    {
        var expr = Parse("let f x = x in f 1");

        var letExpr = expr.ShouldBeOfType<Expr.Let>();
        letExpr.Name.ShouldBe("f");
        letExpr.Value.ShouldBeOfType<Expr.Lambda>();
    }

    [Test]
    public void Parse_should_support_let_sugar_with_multiple_parameters()
    {
        var expr = Parse("let f x y = x in f 1 2");

        var letExpr = expr.ShouldBeOfType<Expr.Let>();
        letExpr.Name.ShouldBe("f");
        var outer = letExpr.Value.ShouldBeOfType<Expr.Lambda>();
        outer.ParamName.ShouldBe("x");
        outer.Body.ShouldBeOfType<Expr.Lambda>().ParamName.ShouldBe("y");
    }

    [Test]
    public void Parse_should_support_type_declaration()
    {
        var diag = new Diagnostics();
        var program = new Parser("type Color = | Red | Green | Blue\nRed", diag).ParseProgram();

        diag.Errors.ShouldBeEmpty();
        program.TypeDecls.Count.ShouldBe(1);
        program.TypeDecls[0].Name.ShouldBe("Color");
        program.TypeDecls[0].Constructors.Count.ShouldBe(3);
    }

    [Test]
    public void Parse_should_support_type_declaration_with_type_parameters()
    {
        var diag = new Diagnostics();
        var program = new Parser("type Pair(a, b) = | MkPair(a, b)\nMkPair(1, 2)", diag).ParseProgram();

        diag.Errors.ShouldBeEmpty();
        program.TypeDecls[0].Name.ShouldBe("Pair");
        program.TypeDecls[0].TypeParameters.Count.ShouldBe(2);
        program.TypeDecls[0].Constructors[0].Name.ShouldBe("MkPair");
    }

    [Test]
    public void Parse_should_support_function_call_with_multiple_arguments()
    {
        var expr = Parse("f 1 2 3");

        // f 1 2 3 → ((f 1) 2) 3 (left-associative application)
        var c3 = expr.ShouldBeOfType<Expr.Call>();
        c3.Arg.ShouldBe(new Expr.IntLit(3));
        var c2 = c3.Func.ShouldBeOfType<Expr.Call>();
        c2.Arg.ShouldBe(new Expr.IntLit(2));
        var c1 = c2.Func.ShouldBeOfType<Expr.Call>();
        c1.Arg.ShouldBe(new Expr.IntLit(1));
        c1.Func.ShouldBe(new Expr.Var("f"));
    }

    [Test]
    public void Parse_should_report_error_for_missing_else_branch()
    {
        var diag = new Diagnostics();
        _ = new Parser("if true then 1", diag).ParseProgram();

        diag.Errors.ShouldNotBeEmpty();
    }

    [Test]
    public void Parse_should_support_negative_integer()
    {
        var expr = Parse("-5");

        // -5 is desugared as 0 - 5
        var sub = expr.ShouldBeOfType<Expr.Subtract>();
        sub.Left.ShouldBe(new Expr.IntLit(0));
        sub.Right.ShouldBe(new Expr.IntLit(5));
    }

    [Test]
    public void Parse_should_support_unit_value()
    {
        var expr = Parse("Unit");

        expr.ShouldBe(new Expr.Var("Unit"));
    }

    [Test]
    public void Parse_should_support_let_question_for_result_binding()
    {
        var expr = Parse("let? x = Ok(1) in x");

        expr.ShouldBeOfType<Expr.LetResult>();
    }

    [Test]
    public void Parse_should_support_constructor_pattern_with_no_args()
    {
        var match = Parse("match x with | None -> 0")
            .ShouldBeOfType<Expr.Match>();

        // None without parens is a variable pattern, not constructor
        match.Cases[0].Pattern.ShouldBeOfType<Pattern.Var>().Name.ShouldBe("None");
    }

    [Test]
    public void Parse_should_support_empty_list_pattern()
    {
        var match = Parse("match xs with | [] -> 0 | x :: rest -> 1")
            .ShouldBeOfType<Expr.Match>();

        match.Cases[0].Pattern.ShouldBeOfType<Pattern.EmptyList>();
    }

    [Test]
    public void Parse_should_support_tuple_pattern_in_match()
    {
        var match = Parse("match p with | (a, b, c) -> a")
            .ShouldBeOfType<Expr.Match>();

        var tuple = match.Cases[0].Pattern.ShouldBeOfType<Pattern.Tuple>();
        tuple.Elements.Count.ShouldBe(3);
    }

    [Test]
    public void Parse_should_support_cons_pattern_in_match()
    {
        var match = Parse("match xs with | h :: t -> h")
            .ShouldBeOfType<Expr.Match>();

        var cons = match.Cases[0].Pattern.ShouldBeOfType<Pattern.Cons>();
        cons.Head.ShouldBeOfType<Pattern.Var>().Name.ShouldBe("h");
        cons.Tail.ShouldBeOfType<Pattern.Var>().Name.ShouldBe("t");
    }

    [Test]
    public void Parse_should_support_let_sugar_params_stored()
    {
        var expr = Parse("let f a b = a in f 1 2");

        var letExpr = expr.ShouldBeOfType<Expr.Let>();
        letExpr.SugarParams.Count.ShouldBe(2);
        letExpr.SugarParams[0].ShouldBe("a");
        letExpr.SugarParams[1].ShouldBe("b");
    }

    [Test]
    public void Parse_should_support_let_rec_sugar_params()
    {
        var expr = Parse("let rec f x y = f y x in f 1 2");

        var letRec = expr.ShouldBeOfType<Expr.LetRec>();
        letRec.SugarParams.Count.ShouldBe(2);
        letRec.SugarParams[0].ShouldBe("x");
        letRec.SugarParams[1].ShouldBe("y");
    }

    [Test]
    public void Parse_should_support_whitespace_application_flag()
    {
        var expr = Parse("f 1");

        var call = expr.ShouldBeOfType<Expr.Call>();
        call.IsWhitespaceApplication.ShouldBeTrue();
    }

    [Test]
    public void Parse_should_support_parenthesized_application()
    {
        var expr = Parse("f(1)");

        var call = expr.ShouldBeOfType<Expr.Call>();
        call.IsWhitespaceApplication.ShouldBeFalse();
    }

    private static Expr Parse(string source)
    {
        var diag = new Diagnostics();
        var expr = new Parser(source, diag).ParseExpression();
        diag.Errors.ShouldBeEmpty();
        return expr;
    }
}
