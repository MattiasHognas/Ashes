using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ParserTests
{
    [Test]
    public void Parse_should_support_empty_and_non_empty_list_literals()
    {
        Parse("[]").ShouldBeOfType<Expr.ListLit>().Elements.ShouldBeEmpty();

        var list = Parse("[1, 2, 3]").ShouldBeOfType<Expr.ListLit>();
        list.Elements.Count.ShouldBe(3);
        list.Elements[0].ShouldBe(new Expr.IntLit(1));
        list.Elements[1].ShouldBe(new Expr.IntLit(2));
        list.Elements[2].ShouldBe(new Expr.IntLit(3));
    }

    [Test]
    public void Parse_should_make_cons_operator_right_associative()
    {
        var cons = Parse("1 :: 2 :: []").ShouldBeOfType<Expr.Cons>();
        cons.Head.ShouldBe(new Expr.IntLit(1));

        var tail = cons.Tail.ShouldBeOfType<Expr.Cons>();
        tail.Head.ShouldBe(new Expr.IntLit(2));
        tail.Tail.ShouldBeOfType<Expr.ListLit>().Elements.ShouldBeEmpty();
    }

    [Test]
    public void Parse_should_support_match_with_list_patterns()
    {
        var match = Parse("match xs with | [] -> 0 | x :: rest -> x").ShouldBeOfType<Expr.Match>();
        match.Value.ShouldBe(new Expr.Var("xs"));
        match.Cases.Count.ShouldBe(2);
        match.Cases[0].Pattern.ShouldBe(new Pattern.EmptyList());
        match.Cases[0].Body.ShouldBe(new Expr.IntLit(0));
        match.Cases[1].Pattern.ShouldBe(new Pattern.Cons(new Pattern.Var("x"), new Pattern.Var("rest")));
        match.Cases[1].Body.ShouldBe(new Expr.Var("x"));
    }

    [Test]
    public void Parse_should_support_tuple_literals_and_grouped_parentheses()
    {
        var tuple = Parse("(1, \"x\")").ShouldBeOfType<Expr.TupleLit>();
        tuple.Elements.Count.ShouldBe(2);
        tuple.Elements[0].ShouldBe(new Expr.IntLit(1));
        tuple.Elements[1].ShouldBe(new Expr.StrLit("x"));
        Parse("(1)").ShouldBe(new Expr.IntLit(1));
    }

    [Test]
    public void Parse_should_treat_empty_paren_call_as_unit_argument()
    {
        var call = Parse("Ashes.IO.readLine()").ShouldBeOfType<Expr.Call>();
        call.Func.ShouldBe(new Expr.QualifiedVar("Ashes.IO", "readLine"));
        call.Arg.ShouldBe(new Expr.Var("Unit"));
    }

    [Test]
    public void Parse_should_support_tuple_patterns()
    {
        var match = Parse("match p with | (a, b) -> a").ShouldBeOfType<Expr.Match>();
        var tuple = match.Cases[0].Pattern.ShouldBeOfType<Pattern.Tuple>();
        tuple.Elements.Count.ShouldBe(2);
        tuple.Elements[0].ShouldBe(new Pattern.Var("a"));
        tuple.Elements[1].ShouldBe(new Pattern.Var("b"));
    }

    [Test]
    public void Parse_should_respect_multiplicative_and_additive_precedence()
    {
        var expr = Parse("1 + 2 * 3 - 4 / 2").ShouldBeOfType<Expr.Subtract>();
        expr.Left.ShouldBe(new Expr.Add(new Expr.IntLit(1), new Expr.Multiply(new Expr.IntLit(2), new Expr.IntLit(3))));
        expr.Right.ShouldBe(new Expr.Divide(new Expr.IntLit(4), new Expr.IntLit(2)));
    }

    [Test]
    public void Parse_should_support_unary_negation_with_higher_precedence_than_multiplication()
    {
        var expr = Parse("-1 * 2").ShouldBeOfType<Expr.Multiply>();
        expr.Left.ShouldBe(new Expr.Subtract(new Expr.IntLit(0), new Expr.IntLit(1)));
        expr.Right.ShouldBe(new Expr.IntLit(2));
    }

    [Test]
    public void Parse_should_support_float_literals()
    {
        Parse("3.14").ShouldBe(new Expr.FloatLit(3.14, "3.14"));
    }

    [Test]
    public void Parse_should_support_negative_float_literals_via_unary_negation()
    {
        Parse("-2.25").ShouldBe(new Expr.Subtract(new Expr.IntLit(0), new Expr.FloatLit(2.25, "2.25")));
    }

    [Test]
    public void Parse_pipe_should_desugar_to_call()
    {
        var expr = Parse("2 |> inc").ShouldBeOfType<Expr.Call>();
        expr.Func.ShouldBe(new Expr.Var("inc"));
        expr.Arg.ShouldBe(new Expr.IntLit(2));
    }

    [Test]
    public void Parse_pipe_should_be_left_associative()
    {
        var expr = Parse("1 |> inc |> double").ShouldBeOfType<Expr.Call>();
        expr.Func.ShouldBe(new Expr.Var("double"));
        expr.Arg.ShouldBe(new Expr.Call(new Expr.Var("inc"), new Expr.IntLit(1)));
    }

    [Test]
    public void Parse_pipe_should_have_lower_precedence_than_arithmetic_and_call()
    {
        var expr = Parse("1 + 2 |> f").ShouldBeOfType<Expr.Call>();
        expr.Func.ShouldBe(new Expr.Var("f"));
        expr.Arg.ShouldBe(new Expr.Add(new Expr.IntLit(1), new Expr.IntLit(2)));

        var callExpr = Parse("1 |> f(2)").ShouldBeOfType<Expr.Call>();
        callExpr.Func.ShouldBe(new Expr.Call(new Expr.Var("f"), new Expr.IntLit(2)));
        callExpr.Arg.ShouldBe(new Expr.IntLit(1));
    }

    [Test]
    public void Parse_result_success_pipe_should_be_left_associative()
    {
        var expr = Parse("Ok(1) |?> inc |?> double").ShouldBeOfType<Expr.ResultPipe>();
        expr.Right.ShouldBe(new Expr.Var("double"));
        expr.Left.ShouldBe(new Expr.ResultPipe(
            new Expr.Call(new Expr.Var("Ok"), new Expr.IntLit(1)),
            new Expr.Var("inc")));
    }

    [Test]
    public void Parse_result_error_pipe_should_share_pipe_precedence()
    {
        var expr = Parse("1 + 2 |!> wrap").ShouldBeOfType<Expr.ResultMapErrorPipe>();
        expr.Left.ShouldBe(new Expr.Add(new Expr.IntLit(1), new Expr.IntLit(2)));
        expr.Right.ShouldBe(new Expr.Var("wrap"));
    }

    [Test]
    public void Parse_mixed_result_pipes_should_be_left_associative()
    {
        var expr = Parse("Ok(1) |?> parse |!> wrap").ShouldBeOfType<Expr.ResultMapErrorPipe>();
        expr.Right.ShouldBe(new Expr.Var("wrap"));
        expr.Left.ShouldBe(new Expr.ResultPipe(
            new Expr.Call(new Expr.Var("Ok"), new Expr.IntLit(1)),
            new Expr.Var("parse")));
    }

    [Test]
    public void Parse_let_result_should_create_dedicated_expression_node()
    {
        var expr = Parse("let? x = Ok(1) in Ok(x)").ShouldBeOfType<Expr.LetResult>();
        expr.Name.ShouldBe("x");
        expr.Value.ShouldBe(new Expr.Call(new Expr.Var("Ok"), new Expr.IntLit(1)));
        expr.Body.ShouldBe(new Expr.Call(new Expr.Var("Ok"), new Expr.Var("x")));
    }

    [Test]
    public void Match_with_non_exhaustive_cases_should_report_diagnostic()
    {
        var diag = new Diagnostics();
        var expr = new Parser("match [] with | _ :: _ -> 1", diag).ParseExpression();
        diag.Errors.ShouldBeEmpty();

        _ = new Lowering(diag).Lower(expr);
        diag.Errors.ShouldContain(x => x.Contains("Non-exhaustive match expression. Missing case: [].", StringComparison.Ordinal));
    }

    [Test]
    public void ParseProgram_should_parse_type_declaration_with_no_arg_constructors()
    {
        var program = ParseProgram("type Color = | Red | Green | Blue\nprint(1)");

        program.TypeDecls.Count.ShouldBe(1);
        var decl = program.TypeDecls[0];
        decl.Name.ShouldBe("Color");
        decl.TypeParameters.ShouldBeEmpty();
        decl.Constructors.Count.ShouldBe(3);
        decl.Constructors[0].Name.ShouldBe("Red");
        decl.Constructors[0].Parameters.ShouldBeEmpty();
        decl.Constructors[1].Name.ShouldBe("Green");
        decl.Constructors[1].Parameters.ShouldBeEmpty();
        decl.Constructors[2].Name.ShouldBe("Blue");
        decl.Constructors[2].Parameters.ShouldBeEmpty();
    }

    [Test]
    public void ParseProgram_should_parse_type_declaration_with_parameterized_constructors()
    {
        var program = ParseProgram("type Maybe = | None | Some(T)\nprint(1)");

        program.TypeDecls.Count.ShouldBe(1);
        var decl = program.TypeDecls[0];
        decl.Name.ShouldBe("Maybe");
        decl.TypeParameters.ShouldBeEmpty();
        decl.Constructors.Count.ShouldBe(2);
        decl.Constructors[0].Name.ShouldBe("None");
        decl.Constructors[0].Parameters.ShouldBeEmpty();
        decl.Constructors[1].Name.ShouldBe("Some");
        decl.Constructors[1].Parameters.ShouldBe(["T"]);
    }

    [Test]
    public void ParseProgram_should_parse_explicit_type_parameters()
    {
        var program = ParseProgram("type Result(E, A) = | Ok(A) | Error(E)\nprint(1)");

        program.TypeDecls.Count.ShouldBe(1);
        var decl = program.TypeDecls[0];
        decl.Name.ShouldBe("Result");
        decl.TypeParameters.Select(x => x.Name).ShouldBe(["E", "A"]);
        decl.Constructors[0].Parameters.ShouldBe(["A"]);
        decl.Constructors[1].Parameters.ShouldBe(["E"]);
    }

    [Test]
    public void ParseProgram_should_parse_multiple_type_declarations()
    {
        var program = ParseProgram("type A = | X\ntype B = | Y(T1, T2)\nprint(1)");

        program.TypeDecls.Count.ShouldBe(2);
        program.TypeDecls[0].Name.ShouldBe("A");
        program.TypeDecls[1].Name.ShouldBe("B");
        program.TypeDecls[1].Constructors[0].Parameters.ShouldBe(["T1", "T2"]);
    }

    [Test]
    public void ParseProgram_should_parse_program_with_no_type_declarations()
    {
        var program = ParseProgram("print(42)");

        program.TypeDecls.ShouldBeEmpty();
        program.Body.ShouldBeOfType<Expr.Call>();
    }

    [Test]
    public void ParseProgram_should_parse_multiline_type_declaration()
    {
        var program = ParseProgram("type Maybe =\n  | None\n  | Some(T)\nprint(1)");

        program.TypeDecls.Count.ShouldBe(1);
        var decl = program.TypeDecls[0];
        decl.Name.ShouldBe("Maybe");
        decl.Constructors.Count.ShouldBe(2);
        decl.Constructors[0].Name.ShouldBe("None");
        decl.Constructors[1].Name.ShouldBe("Some");
        decl.Constructors[1].Parameters.ShouldBe(["T"]);
    }

    [Test]
    public void ParseProgram_should_ignore_line_comments()
    {
        var program = ParseProgram("// expect: ok\ntype Maybe =\n  | None\n  | Some(T)\n\n// body\nprint(1)");

        program.TypeDecls.Count.ShouldBe(1);
        program.Body.ShouldBeOfType<Expr.Call>();
    }

    [Test]
    public void ParseProgram_should_parse_constructor_with_empty_parameter_list()
    {
        var program = ParseProgram("type Foo = | Bar()\nprint(1)");

        program.TypeDecls.Count.ShouldBe(1);
        var ctor = program.TypeDecls[0].Constructors[0];
        ctor.Name.ShouldBe("Bar");
        ctor.Parameters.ShouldBeEmpty();
    }

    [Test]
    public void ParseTypeDecl_with_no_constructors_should_report_diagnostic()
    {
        var diag = new Diagnostics();
        _ = new Parser("type Empty = print(1)", diag).ParseProgram();
        diag.Errors.ShouldContain(x => x.Contains("must have at least one constructor", StringComparison.Ordinal));
    }

    [Test]
    public void TypeDecl_should_support_type_parameters()
    {
        var typeParam = new TypeParameter("'a");
        var ctor = new TypeConstructor("Some", ["'a"]);
        var decl = new TypeDecl("Maybe", [typeParam], [ctor]);

        decl.Name.ShouldBe("Maybe");
        decl.TypeParameters.Count.ShouldBe(1);
        decl.TypeParameters[0].Name.ShouldBe("'a");
        decl.Constructors.Count.ShouldBe(1);
    }

    [Test]
    public void Parse_whitespace_application_single_arg()
    {
        var expr = Parse("f x").ShouldBeOfType<Expr.Call>();
        expr.Func.ShouldBe(new Expr.Var("f"));
        expr.Arg.ShouldBe(new Expr.Var("x"));
        expr.IsWhitespaceApplication.ShouldBeTrue();
    }

    [Test]
    public void Parse_whitespace_application_multi_arg_is_left_associative()
    {
        // f x y => ((f x) y)
        var outer = Parse("f x y").ShouldBeOfType<Expr.Call>();
        outer.Arg.ShouldBe(new Expr.Var("y"));
        outer.IsWhitespaceApplication.ShouldBeTrue();

        var inner = outer.Func.ShouldBeOfType<Expr.Call>();
        inner.Func.ShouldBe(new Expr.Var("f"));
        inner.Arg.ShouldBe(new Expr.Var("x"));
        inner.IsWhitespaceApplication.ShouldBeTrue();
    }

    [Test]
    public void Parse_whitespace_application_integer_arg()
    {
        var expr = Parse("f 42").ShouldBeOfType<Expr.Call>();
        expr.Func.ShouldBe(new Expr.Var("f"));
        expr.Arg.ShouldBe(new Expr.IntLit(42));
        expr.IsWhitespaceApplication.ShouldBeTrue();
    }

    [Test]
    public void Parse_whitespace_application_float_arg()
    {
        var expr = Parse("f 1.5").ShouldBeOfType<Expr.Call>();
        expr.Func.ShouldBe(new Expr.Var("f"));
        expr.Arg.ShouldBe(new Expr.FloatLit(1.5, "1.5"));
        expr.IsWhitespaceApplication.ShouldBeTrue();
    }

    [Test]
    public void Parse_whitespace_application_string_arg()
    {
        var expr = Parse("print \"hello\"").ShouldBeOfType<Expr.Call>();
        expr.Func.ShouldBe(new Expr.Var("print"));
        expr.Arg.ShouldBe(new Expr.StrLit("hello"));
        expr.IsWhitespaceApplication.ShouldBeTrue();
    }

    [Test]
    public void Parse_whitespace_application_bool_arg()
    {
        var expr = Parse("f true").ShouldBeOfType<Expr.Call>();
        expr.Func.ShouldBe(new Expr.Var("f"));
        expr.Arg.ShouldBe(new Expr.BoolLit(true));
        expr.IsWhitespaceApplication.ShouldBeTrue();
    }

    [Test]
    public void Parse_whitespace_application_list_arg()
    {
        var expr = Parse("f [1, 2]").ShouldBeOfType<Expr.Call>();
        expr.Func.ShouldBe(new Expr.Var("f"));
        expr.IsWhitespaceApplication.ShouldBeTrue();
        var list = expr.Arg.ShouldBeOfType<Expr.ListLit>();
        list.Elements.Count.ShouldBe(2);
    }

    [Test]
    public void Parse_whitespace_application_binds_tighter_than_addition()
    {
        // f x + y => (f x) + y
        var expr = Parse("f x + y").ShouldBeOfType<Expr.Add>();
        var call = expr.Left.ShouldBeOfType<Expr.Call>();
        call.Func.ShouldBe(new Expr.Var("f"));
        call.Arg.ShouldBe(new Expr.Var("x"));
        expr.Right.ShouldBe(new Expr.Var("y"));
    }

    [Test]
    public void Parse_whitespace_application_qualified_name()
    {
        var expr = Parse("Ashes.IO.print \"hello\"").ShouldBeOfType<Expr.Call>();
        expr.Func.ShouldBe(new Expr.QualifiedVar("Ashes.IO", "print"));
        expr.Arg.ShouldBe(new Expr.StrLit("hello"));
        expr.IsWhitespaceApplication.ShouldBeTrue();
    }

    [Test]
    public void Parse_nested_module_path()
    {
        var expr = Parse("Ashes.IO.print \"hello\"").ShouldBeOfType<Expr.Call>();
        expr.Func.ShouldBe(new Expr.QualifiedVar("Ashes.IO", "print"));
        expr.Arg.ShouldBe(new Expr.StrLit("hello"));
        expr.IsWhitespaceApplication.ShouldBeTrue();
    }

    [Test]
    public void Parse_deeply_nested_module_path()
    {
        var expr = Parse("Ashes.Net.Http.get").ShouldBeOfType<Expr.QualifiedVar>();
        expr.Module.ShouldBe("Ashes.Net.Http");
        expr.Name.ShouldBe("get");
    }

    [Test]
    public void Parse_whitespace_application_mixed_with_paren_call()
    {
        // f(x) y => (f(x)) y — paren then whitespace
        var outer = Parse("f(1) 2").ShouldBeOfType<Expr.Call>();
        outer.Arg.ShouldBe(new Expr.IntLit(2));
        outer.IsWhitespaceApplication.ShouldBeTrue();

        var inner = outer.Func.ShouldBeOfType<Expr.Call>();
        inner.Func.ShouldBe(new Expr.Var("f"));
        inner.Arg.ShouldBe(new Expr.IntLit(1));
        inner.IsWhitespaceApplication.ShouldBeFalse();
    }

    [Test]
    public void Parse_whitespace_application_does_not_consume_keywords()
    {
        // "if" should not be treated as a whitespace argument
        var ifExpr = Parse("if f x then 1 else 2").ShouldBeOfType<Expr.If>();
        var call = ifExpr.Cond.ShouldBeOfType<Expr.Call>();
        call.Func.ShouldBe(new Expr.Var("f"));
        call.Arg.ShouldBe(new Expr.Var("x"));
    }

    [Test]
    public void Parse_should_report_trailing_tokens_after_float_literal()
    {
        var diag = new Diagnostics();

        _ = new Parser("1..2", diag).ParseExpression();

        diag.Errors.ShouldContain(x => x.Contains("Unexpected token after end of expression: Dot.", StringComparison.Ordinal));
    }

    private static Expr Parse(string source)
    {
        var diag = new Diagnostics();
        var expr = new Parser(source, diag).ParseExpression();
        diag.Errors.ShouldBeEmpty();
        return expr;
    }

    private static Program ParseProgram(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.Errors.ShouldBeEmpty();
        return program;
    }
}
