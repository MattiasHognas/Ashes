using Ashes.Formatter;
using Shouldly;

namespace Ashes.Tests;

public sealed class CommentReinserterTests
{
    private static string? FormatToFixedPoint(string source)
    {
        return CommentReinserter.ToFixedPoint(source, current =>
        {
            var diagnostics = new Ashes.Frontend.Diagnostics();
            var program = new Ashes.Frontend.Parser(current, diagnostics).ParseProgram();
            if (diagnostics.Errors.Count > 0)
            {
                return null;
            }

            var formatted = Ashes.Formatter.Formatter.Format(
                program,
                preferPipelines: current.Contains("|>", StringComparison.Ordinal),
                options: new FormattingOptions { NewLine = "\n" });
            return CommentReinserter.ReinsertStandaloneCommentLines(current, formatted, "\n");
        });
    }

    [Test]
    public void ToFixedPoint_converges_to_a_stable_result_in_one_call()
    {
        // Nested `let/in` chains reformat into a growing-indentation pyramid; a comment attached
        // to a middle `let` can need more than one internal pass before its anchor resolves
        // against the pyramid's final shape. A single ToFixedPoint call must already return
        // whatever a caller manually re-running the formatter 2-3 times would have converged to.
        const string source = """
            let a = 1
            in
            let b = 2
            in
            // comment before c
            let c = a + b
            in
            h + c
            """;

        var once = FormatToFixedPoint(source);
        once.ShouldNotBeNull();

        var twice = FormatToFixedPoint(once);
        twice.ShouldBe(once, "formatting an already-fixed-point result must be a no-op");
    }

    [Test]
    public void ToFixedPoint_keeps_a_comment_near_its_anchor_when_in_merges_onto_the_next_line()
    {
        // A comment anchored to a trailing expression must not jump to the top of the file just
        // because the formatter chose to merge the preceding `in` onto the same physical line
        // (`in h + g` vs. `in` / `h + g`) — the two forms are the same code, and the comment's
        // anchor line's token signature must match either way.
        const string source = """
            let g = 1
            in
            let h = 2
            in
            // comment before final
            h + g
            """;

        var formatted = FormatToFixedPoint(source);
        formatted.ShouldNotBeNull();

        var commentLineIndex = Array.FindIndex(
            formatted.Split('\n'),
            line => line.Contains("comment before final", StringComparison.Ordinal));
        var lastCodeLineIndex = Array.FindLastIndex(
            formatted.Split('\n'),
            line => line.Contains("h + g", StringComparison.Ordinal));

        commentLineIndex.ShouldBeGreaterThan(0, "the comment must not have fallen back to the top of the file");
        (lastCodeLineIndex - commentLineIndex).ShouldBeLessThanOrEqualTo(
            1,
            "the comment must land immediately before the `h + g` line it was anchored to, not drift elsewhere");
    }

    [Test]
    public void ToFixedPoint_keeps_a_comment_before_a_definition_the_formatter_collapses_onto_one_line()
    {
        // Both of the comment's anchors change here: the definition above it collapses onto one
        // line (its last line `x + 1` becomes that line's tail) and the definition below it
        // collapses too (its first line `let b (flag: Bool) =` becomes that line's head). The
        // comment must stay in front of `let b`, matched through the merged line, instead of
        // falling back to the top of the file.
        const string source = """
            let a (x: Int) =
                x + 1

            // comment for b
            let b (flag: Bool) =
                flag == false && true

            b(a(1) == 2)
            """;

        var formatted = FormatToFixedPoint(source);
        formatted.ShouldNotBeNull();

        var lines = formatted.Split('\n');
        var commentLineIndex = Array.FindIndex(lines, line => line.Contains("comment for b", StringComparison.Ordinal));
        var definitionLineIndex = Array.FindIndex(lines, line => line.StartsWith("let b", StringComparison.Ordinal));

        definitionLineIndex.ShouldBeGreaterThan(0);
        commentLineIndex.ShouldBe(definitionLineIndex - 1, "the comment must stay directly in front of the definition it documented");
    }

    [Test]
    public void GetLineSignature_treats_a_bare_in_line_and_a_merged_in_line_as_the_same_anchor()
    {
        var mergedFormatted = FormatToFixedPoint("""
            let x = 1
            in
            // c
            x
            """);
        mergedFormatted.ShouldNotBeNull();

        // Re-running the fixed-point formatter on its own output must be a true no-op: this is
        // the same idempotence property as the test above, phrased directly against the
        // `in`-merging case rather than the pyramid-depth case.
        FormatToFixedPoint(mergedFormatted).ShouldBe(mergedFormatted);
    }
}
