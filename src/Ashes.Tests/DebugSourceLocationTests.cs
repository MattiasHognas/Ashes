using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Debug source locations for module-stitched compilations: instructions lowered from the
/// entry file must carry the entry file path with the user's original line numbers, and
/// stitching glue must carry no location at all (the backend then emits the DWARF line-0
/// convention for compiler-generated code).
/// </summary>
public sealed class DebugSourceLocationTests
{
    [Test]
    public void Entry_locations_keep_original_line_numbers_when_imports_are_present()
    {
        var source = """
            import Ashes.Text
            import Ashes.Collection.List

            let greeting = "hello world"

            let numbers = [1, 2, 3]

            let flag = Ashes.Text.length(greeting) > 5

            let count =
                if flag
                then Ashes.Collection.List.length(numbers)
                else 0

            Ashes.IO.print(count)
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var ir = LowerWithLayout(source, "/tmp/entry-lines.ash");

        var entryLocations = ir.EntryFunction.Instructions
            .Where(inst => inst.Location is { } loc && loc.FilePath.EndsWith("entry-lines.ash", StringComparison.Ordinal))
            .Select(inst => inst.Location!.Value)
            .ToArray();

        entryLocations.ShouldNotBeEmpty();

        // Original file lines: greeting=4, numbers=6, flag=8, count=10..13, print=15.
        var lines = entryLocations.Select(loc => loc.Line).Distinct().Order().ToArray();
        lines.ShouldContain(4);
        lines.ShouldContain(6);
        lines.ShouldContain(8);
        lines.ShouldContain(15);

        // Nothing may map beyond the entry file's real line count: a larger line means a
        // combined-source position leaked through (the pre-fix failure mode attributed the
        // stitching boundary to the entry file at its combined-source line).
        var sourceLineCount = source.Count(c => c == '\n') + 1;
        lines[^1].ShouldBeLessThanOrEqualTo(sourceLineCount);
    }

    [Test]
    public void Import_free_entry_locations_are_unchanged_by_the_layout_mapping()
    {
        var source = """
            let answer = 41

            Ashes.IO.print(answer + 1)
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var ir = LowerWithLayout(source, "/tmp/plain-lines.ash");

        var lines = ir.EntryFunction.Instructions
            .Where(inst => inst.Location is { } loc && loc.FilePath.EndsWith("plain-lines.ash", StringComparison.Ordinal))
            .Select(inst => inst.Location!.Value.Line)
            .Distinct()
            .Order()
            .ToArray();

        lines.ShouldBe([1, 3]);
    }

    private static IrProgram LowerWithLayout(string source, string displayPath)
    {
        var parsed = ProjectSupport.ParseImportHeader(source, displayPath);
        var layout = ProjectSupport.BuildStandaloneCompilationLayout(
            parsed.SourceWithoutImports, parsed.ImportNames, displayPath);

        var diag = new Diagnostics();
        var program = new Parser(layout.Source, diag).ParseProgram();
        diag.StructuredErrors.ShouldBeEmpty();

        var importedStd = parsed.ImportNames.ToHashSet(StringComparer.Ordinal);
        var lowering = new Lowering(diag, importedStd.Count == 0 ? null : importedStd, null);
        lowering.SetSourceContext(layout);
        var ir = lowering.Lower(program);
        diag.StructuredErrors.ShouldBeEmpty();
        return ir;
    }
}
