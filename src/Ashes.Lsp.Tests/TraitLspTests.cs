using Ashes.Frontend;
using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class TraitLspTests
{
    private const string Source =
        "trait Render(a) =\n"
        + "    | render : a -> Str\n\n"
        + "implement Render(Int) =\n"
        + "    | render = given (_) -> \"int\"\n\n"
        + "let display : a -> Str requires {Render(a)} =\n"
        + "    given (value) -> Render.render(value)\n\n"
        + "display(1)";

    [Test]
    public void Traits_are_analyzed_and_formatted_through_the_LSP_pipeline()
    {
        DocumentService.Analyze(Source).ShouldBeEmpty();

        string unformatted = "trait Render(a)=| render:a->Str\nimplement Render(Int)=| render=given (_)->\"int\"\n0";
        string? formatted = DocumentService.Format(unformatted);

        formatted.ShouldNotBeNull();
        formatted.ShouldContain("trait Render(a) =\n    | render : a -> Str");
        formatted.ShouldContain("implement Render(Int) =\n    | render =\n        given (_) -> \"int\"");
    }

    [Test]
    public void Hover_exposes_traits_methods_implementations_and_constraints()
    {
        DocumentService.HoverItem? trait = DocumentService.GetHover(Source, Source.IndexOf("Render(a)", StringComparison.Ordinal));
        DocumentService.HoverItem? method = DocumentService.GetHover(Source, Source.IndexOf("render :", StringComparison.Ordinal));
        int requirementPosition = Source.IndexOf("requires {Render", StringComparison.Ordinal) + "requires {".Length;
        DocumentService.HoverItem? requirement = DocumentService.GetHover(Source, requirementPosition);
        int implementationMethodPosition = Source.IndexOf("render =", StringComparison.Ordinal);
        DocumentService.HoverItem? implementationMethod = DocumentService.GetHover(Source, implementationMethodPosition);

        trait.ShouldNotBeNull();
        trait.Value.Contents.ShouldBe("```ashes\ntrait Render(a)\n```\n\n*trait*");
        method.ShouldNotBeNull();
        method.Value.Contents.ShouldBe("```ashes\nRender.render : a -> Str requires {Render(a)}\n```\n\n*trait method*");
        requirement.ShouldNotBeNull();
        requirement.Value.Contents.ShouldBe("```ashes\ntrait Render(a)\n```\n\n*trait*");
        implementationMethod.ShouldNotBeNull();
        implementationMethod.Value.Contents.ShouldBe("```ashes\nRender.render : a -> Str requires {Render(a)}\n```\n\n*trait method*");
    }

    [Test]
    public void Trait_method_completion_is_qualified_and_deterministic()
    {
        string source = Source + "\nRender.";
        IReadOnlyList<string> completions = DocumentService.GetCompletions(source, source.Length);

        completions.ShouldBe(["render"]);
    }

    [Test]
    public void Definitions_resolve_implementation_heads_bindings_and_qualified_method_calls()
    {
        int traitStart = Source.IndexOf("trait Render", StringComparison.Ordinal);
        TextSpan traitSpan = AstSpans.GetOrDefault(ParseProgram().Items.OfType<TopLevelItem.Trait>().Single().Decl);
        TextSpan methodSpan = AstSpans.GetOrDefault(ParseProgram().Items.OfType<TopLevelItem.Trait>().Single().Decl.Methods.Single());

        DocumentService.DefinitionItem? implementationHead = DocumentService.GetDefinition(
            Source,
            Source.IndexOf("Render(Int)", StringComparison.Ordinal));
        DocumentService.DefinitionItem? implementationBinding = DocumentService.GetDefinition(
            Source,
            Source.IndexOf("render =", StringComparison.Ordinal));
        DocumentService.DefinitionItem? methodCall = DocumentService.GetDefinition(
            Source,
            Source.LastIndexOf("render(value)", StringComparison.Ordinal));

        traitStart.ShouldBe(0);
        implementationHead.ShouldNotBeNull();
        implementationHead.Value.Span.ShouldBe(traitSpan);
        implementationBinding.ShouldNotBeNull();
        implementationBinding.Value.Span.ShouldBe(methodSpan);
        methodCall.ShouldNotBeNull();
        methodCall.Value.Span.ShouldBe(methodSpan);
    }

    [Test]
    public void References_keep_trait_and_method_identities_separate()
    {
        IReadOnlyList<DocumentService.ReferenceItem> traitReferences = DocumentService.GetReferences(
            Source,
            Source.IndexOf("trait Render", StringComparison.Ordinal) + "trait ".Length);
        IReadOnlyList<DocumentService.ReferenceItem> methodReferences = DocumentService.GetReferences(
            Source,
            Source.IndexOf("render :", StringComparison.Ordinal));

        traitReferences.Count.ShouldBe(4);
        methodReferences.Count.ShouldBe(3);
        traitReferences.All(reference => string.Equals(
            Source[reference.Start..reference.End],
            "Render",
            StringComparison.Ordinal)).ShouldBeTrue();
        methodReferences.All(reference => string.Equals(
            Source[reference.Start..reference.End],
            "render",
            StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Test]
    public void Semantic_tokens_classify_trait_names_methods_and_parameters()
    {
        IReadOnlyList<DocumentService.SemanticTokenItem> tokens = DocumentService.GetSemanticTokens(Source);

        tokens.ShouldContain(token => token.Line == 0 && token.Character == 6 && token.TokenType == DocumentService.TokenTypeTrait);
        tokens.ShouldContain(token => token.Line == 0 && token.Character == 13 && token.TokenType == DocumentService.TokenTypeTypeParameter);
        tokens.ShouldContain(token => token.Line == 1 && token.Character == 6 && token.TokenType == DocumentService.TokenTypeTraitMethod);
        tokens.ShouldContain(token => token.Line == 6 && token.Character == 33 && token.TokenType == DocumentService.TokenTypeTrait);
    }

    private static Ashes.Frontend.Program ParseProgram()
    {
        var diagnostics = new Diagnostics();
        Ashes.Frontend.Program program = new Parser(Source, diagnostics).ParseProgram();
        diagnostics.StructuredErrors.ShouldBeEmpty();
        return program;
    }
}
