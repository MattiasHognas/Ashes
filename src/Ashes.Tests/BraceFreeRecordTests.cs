using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Covers the brace-free record surface syntax (LANGUAGE_SPEC §4.1):
/// declarations (<c>type T = | f: T</c>), named-argument construction
/// (<c>T(f = e)</c>), and the bare <c>with</c> update expression.
/// </summary>
public sealed class BraceFreeRecordTests
{
    // ---- Declarations ----------------------------------------------------

    [Test]
    public void Parse_should_accept_brace_free_record_declaration()
    {
        var program = ParseProgram("type Point =\n    | x: Int\n    | y: Int\n0");

        program.TypeDecls.Count.ShouldBe(1);
        var decl = program.TypeDecls[0];
        decl.Name.ShouldBe("Point");
        decl.IsRecord.ShouldBeTrue();
        decl.Constructors.Count.ShouldBe(1);
        var ctor = decl.Constructors[0];
        ctor.Name.ShouldBe("Point");
        ctor.FieldNames.ShouldBe(["x", "y"]);
        ctor.Parameters.SelectMany(p => p.MentionedNames()).ShouldBe(["Int", "Int"]);
    }

    [Test]
    public void Parse_should_accept_single_field_brace_free_record()
    {
        var decl = ParseProgram("type Wrapper =\n    | value: Int\n0").TypeDecls[0];
        decl.IsRecord.ShouldBeTrue();
        decl.Constructors[0].FieldNames.ShouldBe(["value"]);
    }

    [Test]
    public void Parse_should_reject_mixing_field_and_constructor_branches()
    {
        var diag = new Diagnostics();
        _ = new Parser("type Bad =\n    | x: Int\n    | Other(T)\n0", diag).ParseProgram();
        diag.Errors.ShouldContain(e => e.Contains("cannot be mixed", StringComparison.Ordinal));
    }

    // ---- Construction ----------------------------------------------------

    [Test]
    public void Parse_should_treat_uppercase_named_args_as_record_literal()
    {
        var lit = Parse("Point(x = 1, y = 2)").ShouldBeOfType<Expr.RecordLit>();
        lit.TypeName.ShouldBe("Point");
        lit.Fields.Count.ShouldBe(2);
        lit.Fields[0].Name.ShouldBe("x");
        lit.Fields[0].Value.ShouldBe(new Expr.IntLit(1));
        lit.Fields[1].Name.ShouldBe("y");
        lit.Fields[1].Value.ShouldBe(new Expr.IntLit(2));
    }

    [Test]
    public void Parse_should_keep_positional_constructor_calls_as_calls()
    {
        // Equality (==) inside a positional argument must not be mistaken for a named argument.
        var call = Parse("Some(x == 1)").ShouldBeOfType<Expr.Call>();
        call.Func.ShouldBe(new Expr.Var("Some"));
        call.Arg.ShouldBeOfType<Expr.Equal>();
    }

    [Test]
    public void Parse_should_reject_named_arguments_on_non_record_calls()
    {
        var diag = new Diagnostics();
        _ = new Parser("f(x = 1)", diag).ParseExpression();
        diag.Errors.ShouldContain(e => e.Contains("Named arguments are only allowed in record construction", StringComparison.Ordinal));
    }

    // ---- Update ----------------------------------------------------------

    [Test]
    public void Parse_should_accept_brace_free_record_update()
    {
        var update = Parse("p with x = 5").ShouldBeOfType<Expr.RecordUpdate>();
        update.Target.ShouldBe(new Expr.Var("p"));
        update.Updates.Count.ShouldBe(1);
        update.Updates[0].Name.ShouldBe("x");
        update.Updates[0].Value.ShouldBe(new Expr.IntLit(5));
    }

    [Test]
    public void Parse_should_accept_multi_field_record_update()
    {
        var update = Parse("p with x = 5, y = 6").ShouldBeOfType<Expr.RecordUpdate>();
        update.Updates.Select(u => u.Name).ShouldBe(["x", "y"]);
    }

    [Test]
    public void Parse_should_make_chained_with_left_associative()
    {
        var outer = Parse("p with x = 1 with y = 2").ShouldBeOfType<Expr.RecordUpdate>();
        outer.Updates.Single().Name.ShouldBe("y");
        var inner = outer.Target.ShouldBeOfType<Expr.RecordUpdate>();
        inner.Updates.Single().Name.ShouldBe("x");
        inner.Target.ShouldBe(new Expr.Var("p"));
    }

    [Test]
    public void Parse_should_bind_with_looser_than_application_and_arithmetic()
    {
        var update = Parse("f p with x = a + b").ShouldBeOfType<Expr.RecordUpdate>();
        var target = update.Target.ShouldBeOfType<Expr.Call>();
        target.Func.ShouldBe(new Expr.Var("f"));
        target.Arg.ShouldBe(new Expr.Var("p"));
        update.Updates.Single().Value.ShouldBeOfType<Expr.Add>();
    }

    [Test]
    public void Parse_should_not_confuse_match_scrutinee_with_record_update()
    {
        var match = Parse("match p with | Point(x, y) -> x").ShouldBeOfType<Expr.Match>();
        match.Value.ShouldBe(new Expr.Var("p"));
        match.Cases.Count.ShouldBe(1);
    }

    [Test]
    public void Parse_should_allow_record_update_in_parentheses()
    {
        Parse("(p with x = 5)").ShouldBeOfType<Expr.RecordUpdate>();
    }

    [Test]
    public void Parse_should_allow_parenthesised_record_update_as_match_scrutinee()
    {
        var match = Parse("match (p with x = 5) with | q -> q").ShouldBeOfType<Expr.Match>();
        match.Value.ShouldBeOfType<Expr.RecordUpdate>();
    }

    // ---- Curly-brace record syntax rejected ------------------------------

    [Test]
    public void Parse_should_reject_brace_record_declaration()
    {
        var diag = new Diagnostics();
        _ = new Parser("type Point = { x: Int, y: Int }\n0", diag).ParseProgram();
        diag.Errors.ShouldContain(e => e.Contains("Records are declared with '| field: Type', not braces", StringComparison.Ordinal));
    }

    [Test]
    public void Parse_should_reject_brace_record_construction()
    {
        var diag = new Diagnostics();
        _ = new Parser("Point { x = 1, y = 2 }", diag).ParseExpression();
        diag.Errors.ShouldContain(e => e.Contains("Records are constructed with 'Name(field = value)', not braces", StringComparison.Ordinal));
    }

    [Test]
    public void Parse_should_reject_brace_record_update()
    {
        var diag = new Diagnostics();
        _ = new Parser("{ p with x = 1 }", diag).ParseExpression();
        diag.Errors.ShouldContain(e => e.Contains("Records are updated with 'base with field = value', not braces", StringComparison.Ordinal));
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
