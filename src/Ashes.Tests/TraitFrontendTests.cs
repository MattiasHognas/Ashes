using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class TraitFrontendTests
{
    [Test]
    public void LexerReservesTraitImplementRequiresAndDerivingKeywords()
    {
        Diagnostics diagnostics = new();
        Lexer lexer = new("trait implement requires deriving", diagnostics);

        lexer.Next().Kind.ShouldBe(TokenKind.Trait);
        lexer.Next().Kind.ShouldBe(TokenKind.Implement);
        lexer.Next().Kind.ShouldBe(TokenKind.Requires);
        lexer.Next().Kind.ShouldBe(TokenKind.Deriving);
        lexer.Next().Kind.ShouldBe(TokenKind.EOF);
        diagnostics.Errors.ShouldBeEmpty();

        Lexer formerSpelling = new("instance", new Diagnostics());
        formerSpelling.Next().Kind.ShouldBe(TokenKind.Ident);
    }

    [Test]
    public void ParserAndFormatterPreserveCanonicalDerivingClauses()
    {
        const string source = "type Box(a)=| value:a deriving {Show,Eq,Hash}";

        Ashes.Frontend.Program program = ParseProgram(source, out Diagnostics diagnostics);
        TypeDecl declaration = program.TypeDecls.Single();
        string formatted = Ashes.Formatter.Formatter.Format(program);
        Ashes.Frontend.Program reparsed = ParseProgram(formatted, out Diagnostics reparsedDiagnostics);

        diagnostics.Errors.ShouldBeEmpty();
        reparsedDiagnostics.Errors.ShouldBeEmpty(formatted);
        declaration.Deriving.ShouldBe(["Show", "Eq", "Hash"]);
        formatted.ShouldBe("type Box(a) =\n    | value: a\n    deriving {Show, Eq, Hash}\n");
        Ashes.Formatter.Formatter.Format(reparsed).ShouldBe(formatted);
    }

    [Test]
    public void ParserBuildsTraitAndGenericTraitImplementationDeclarationsWithSpans()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool
                | notEqual : a -> a -> Bool = given (left) -> given (right) -> !Eq.equal(left)(right)

            implement Eq(List(a)) requires {Eq(a)} =
                | equal = given (left) -> given (right) -> true
            """;

        Ashes.Frontend.Program program = ParseProgram(source, out Diagnostics diagnostics);

        diagnostics.Errors.ShouldBeEmpty();
        TraitDecl trait = program.Items[0].ShouldBeOfType<TopLevelItem.Trait>().Decl;
        trait.Name.ShouldBe("Eq");
        trait.TypeParameters.ShouldBe([new TypeParameter("a")]);
        trait.Methods.Count.ShouldBe(2);
        trait.Methods[0].DefaultImplementation.ShouldBeNull();
        trait.Methods[1].DefaultImplementation.ShouldNotBeNull();
        AstSpans.GetOrDefault(trait).Length.ShouldBeGreaterThan(0);
        AstSpans.GetOrDefault(trait.Methods[1]).Length.ShouldBeGreaterThan(0);

        TraitImplementationDecl implementation = program.Items[1].ShouldBeOfType<TopLevelItem.Implementation>().Decl;
        implementation.TraitName.ShouldBe("Eq");
        TypeExpr.Applied listType = implementation.TypeArgs.Single().ShouldBeOfType<TypeExpr.Applied>();
        listType.Name.ShouldBe("List");
        listType.Args.Single().ShouldBe(new TypeExpr.Named("a"));
        TraitConstraintSyntax requirement = implementation.Requirements.Single();
        requirement.TraitName.ShouldBe("Eq");
        requirement.TypeArgs.Single().ShouldBe(new TypeExpr.Named("a"));
        implementation.Bindings.Single().MethodName.ShouldBe("equal");
        AstSpans.GetOrDefault(implementation).Length.ShouldBeGreaterThan(0);
        AstSpans.GetOrDefault(implementation.Bindings[0]).Length.ShouldBeGreaterThan(0);
    }

    [Test]
    public void RequiresAttachesToTheCompleteAnnotatedSchemeSeparatelyFromNeeds()
    {
        Expr.Let expression = ParseExpression(
            "let render : a -> Str needs {Log} requires {Ashes.Trait.Show(a), Eq(a)} = given (value) -> value in render")
            .ShouldBeOfType<Expr.Let>();

        TypeExpr.Arrow annotation = expression.TypeAnnotation.ShouldBeOfType<TypeExpr.Arrow>();
        annotation.Needs.ShouldNotBeNull();
        annotation.Needs.Capabilities.Single().Name.ShouldBe("Log");
        expression.Requires.Select(constraint => constraint.TraitName)
            .ShouldBe(["Ashes.Trait.Show", "Eq"]);
    }

    [Test]
    public void FormatterCanonicalizesConstraintsAndIsIdempotent()
    {
        const string source = """
            trait Ord(a) requires {Show(a), Eq(a)} =
              | compare:a->a->Ordering
              | less:a->a->Bool=given (left)->given (right)->false
            implement Ord(List(a)) requires {Show(a), Eq(a)} =
              | compare=given (left)->given (right)->Equal
            let recursive first : a -> a requires {Show(a), Eq(a)} = given (value)->value
            """;

        Ashes.Frontend.Program parsed = ParseProgram(source, out Diagnostics firstDiagnostics);
        string formatted = Ashes.Formatter.Formatter.Format(parsed);
        Ashes.Frontend.Program reparsed = ParseProgram(formatted, out Diagnostics secondDiagnostics);

        firstDiagnostics.Errors.ShouldBeEmpty();
        secondDiagnostics.Errors.ShouldBeEmpty(formatted);
        Ashes.Formatter.Formatter.Format(reparsed).ShouldBe(formatted);
        formatted.ShouldContain("requires {Eq(a), Show(a)}");
    }

    [Test]
    public void BitwiseOrInsideAnImplementationBodySurvivesFormattingAndReparsing()
    {
        const string source = """
            trait BitOr(a) =
                | bitOr : a -> a -> a

            implement BitOr(Int) =
                | bitOr = given (left) -> given (right) -> left | right
            """;

        Ashes.Frontend.Program parsed = ParseProgram(source, out Diagnostics firstDiagnostics);
        string formatted = Ashes.Formatter.Formatter.Format(parsed);
        Ashes.Frontend.Program reparsed = ParseProgram(formatted, out Diagnostics secondDiagnostics);

        firstDiagnostics.Errors.ShouldBeEmpty();
        secondDiagnostics.Errors.ShouldBeEmpty(formatted);
        Ashes.Formatter.Formatter.Format(reparsed).ShouldBe(formatted);
    }

    [Test]
    public void RecursiveGroupsPreserveEachWrittenConstraintList()
    {
        const string source = """
            let recursive first : a -> a requires {Eq(a)} = given (value) -> second(value)
            and second : a -> a requires {Show(a)} = given (value) -> first(value)
            """;

        Ashes.Frontend.Program program = ParseProgram(source, out Diagnostics diagnostics);
        TopLevelItem.RecursiveGroup group = program.Items.Single().ShouldBeOfType<TopLevelItem.RecursiveGroup>();

        diagnostics.Errors.ShouldBeEmpty();
        group.TypeAnnotations.Count.ShouldBe(2);
        group.Requires[0].Single().TraitName.ShouldBe("Eq");
        group.Requires[1].Single().TraitName.ShouldBe("Show");
    }

    [Test]
    [Arguments("trait Broken = | equal : Int -> Int")]
    [Arguments("trait Broken(a) requires {} = | equal : a -> a")]
    [Arguments("implement Eq() = | equal = true")]
    [Arguments("implement Eq(Int) requires {} = | equal = true")]
    [Arguments("type Empty = | Empty deriving {}")]
    public void MalformedTraitDeclarationsProduceBoundedDiagnostics(string source)
    {
        Ashes.Frontend.Program program = ParseProgram(source, out Diagnostics diagnostics);

        program.ShouldNotBeNull();
        diagnostics.Errors.ShouldNotBeEmpty();
        diagnostics.Errors.Count.ShouldBeLessThan(10);
    }

    private static Ashes.Frontend.Program ParseProgram(string source, out Diagnostics diagnostics)
    {
        diagnostics = new Diagnostics();
        return new Parser(source, diagnostics).ParseProgram();
    }

    private static Expr ParseExpression(string source)
    {
        Diagnostics diagnostics = new();
        Expr expression = new Parser(source, diagnostics).ParseExpression();
        diagnostics.Errors.ShouldBeEmpty();
        return expression;
    }
}
