using Ashes.Lsp;
using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class PatternLanguageLspTests
{
    [Test]
    public void Definition_resolves_as_pattern_alias()
    {
        const string source = "match [1] with | head :: _ as whole -> whole";

        DocumentService.DefinitionItem? definition = DocumentService.GetDefinition(
            source,
            source.LastIndexOf("whole", StringComparison.Ordinal));

        definition.ShouldNotBeNull();
        definition.Value.Start.ShouldBe(source.IndexOf("whole", StringComparison.Ordinal));
        definition.Value.End.ShouldBe(definition.Value.Start + "whole".Length);
    }

    [Test]
    public void Completion_includes_or_pattern_shared_binding()
    {
        const string source = "match 1 with | 1 as value | 2 as value -> value";
        int bodyPosition = source.LastIndexOf("value", StringComparison.Ordinal);

        IReadOnlyList<string> completions = DocumentService.GetCompletions(source, bodyPosition);

        completions.ShouldContain("value");
    }

    [Test]
    public void Hover_reports_as_pattern_alias_type()
    {
        const string source = "match [1] with | head :: _ as whole -> whole";

        DocumentService.HoverItem? hover = DocumentService.GetHover(
            source,
            source.IndexOf("whole", StringComparison.Ordinal));

        hover.ShouldNotBeNull();
        hover.Value.Contents.ShouldContain("whole");
        hover.Value.Contents.ShouldContain("List<Int>");
    }
}
