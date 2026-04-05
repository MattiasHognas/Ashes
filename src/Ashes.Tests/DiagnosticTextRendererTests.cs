using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class DiagnosticTextRendererTests
{
    [Test]
    public void RenderCompilerDiagnostics_should_render_unknown_error_when_entries_are_empty()
    {
        var result = DiagnosticTextRenderer.RenderCompilerDiagnostics(
            Array.Empty<DiagnosticEntry>(), "let x = 1", "test.ash");

        result.ShouldContain("Unknown compiler error.");
        result.ShouldContain("test.ash");
    }

    [Test]
    public void RenderCompilerDiagnostics_should_render_single_error_with_source_and_underline()
    {
        var entries = new[]
        {
            new DiagnosticEntry(TextSpan.FromBounds(4, 7), "Type mismatch.", DiagnosticCodes.TypeMismatch)
        };

        var result = DiagnosticTextRenderer.RenderCompilerDiagnostics(entries, "let abc = 1", "main.ash");

        result.ShouldContain("main.ash:1:5");
        result.ShouldContain(DiagnosticCodes.TypeMismatch);
        result.ShouldContain("Type mismatch.");
        result.ShouldContain("let abc = 1");
        result.ShouldContain("^^^");
    }

    [Test]
    public void RenderCompilerDiagnostics_should_render_error_without_code()
    {
        var entries = new[]
        {
            new DiagnosticEntry(TextSpan.FromBounds(0, 1), "Something went wrong.")
        };

        var result = DiagnosticTextRenderer.RenderCompilerDiagnostics(entries, "x", "test.ash");

        result.ShouldContain("test.ash:1:1");
        result.ShouldContain("Something went wrong.");
        // No diagnostic code should appear between path and message
        result.ShouldContain("test.ash:1:1 Something went wrong.");
    }

    [Test]
    public void RenderCompilerDiagnostics_should_render_without_source()
    {
        var entries = new[]
        {
            new DiagnosticEntry(TextSpan.FromBounds(0, 5), "Some error.", DiagnosticCodes.ParseError)
        };

        var result = DiagnosticTextRenderer.RenderCompilerDiagnostics(entries, null, "file.ash");

        result.ShouldContain("file.ash");
        result.ShouldContain(DiagnosticCodes.ParseError);
        result.ShouldContain("Some error.");
        result.ShouldNotContain("|");
    }

    [Test]
    public void RenderCompilerDiagnostics_should_order_multiple_errors_by_position()
    {
        var entries = new[]
        {
            new DiagnosticEntry(TextSpan.FromBounds(10, 12), "Second error."),
            new DiagnosticEntry(TextSpan.FromBounds(0, 3), "First error.")
        };

        var result = DiagnosticTextRenderer.RenderCompilerDiagnostics(entries, "let x = let y = 1 in y", "test.ash");

        var firstIndex = result.IndexOf("First error.", StringComparison.Ordinal);
        var secondIndex = result.IndexOf("Second error.", StringComparison.Ordinal);
        firstIndex.ShouldBeLessThan(secondIndex);
    }

    [Test]
    public void RenderCompilerDiagnostics_should_handle_multiline_source()
    {
        var source = "let x = 1\nlet y = 2";
        var entries = new[]
        {
            new DiagnosticEntry(TextSpan.FromBounds(14, 15), "Error on line 2.")
        };

        var result = DiagnosticTextRenderer.RenderCompilerDiagnostics(entries, source, "test.ash");

        result.ShouldContain("test.ash:2:");
        result.ShouldContain("let y = 2");
    }

    [Test]
    public void RenderCompilerDiagnostics_should_handle_crlf_line_endings()
    {
        var source = "let x = 1\r\nlet y = 2";
        var entries = new[]
        {
            new DiagnosticEntry(TextSpan.FromBounds(15, 16), "Error on line 2.")
        };

        var result = DiagnosticTextRenderer.RenderCompilerDiagnostics(entries, source, "test.ash");

        result.ShouldContain("test.ash:2:");
    }

    [Test]
    public void RenderCompilerDiagnostics_should_handle_error_at_start_of_source()
    {
        var entries = new[]
        {
            new DiagnosticEntry(TextSpan.FromBounds(0, 0), "Error at start.")
        };

        var result = DiagnosticTextRenderer.RenderCompilerDiagnostics(entries, "x", "test.ash");

        result.ShouldContain("test.ash:1:1");
        result.ShouldContain("^");
    }

    [Test]
    public void RenderCompilerDiagnostics_from_exception_should_delegate_to_entries_overload()
    {
        var entries = new[]
        {
            new DiagnosticEntry(TextSpan.FromBounds(0, 3), "Oops.", DiagnosticCodes.ParseError)
        };
        var exception = new CompileDiagnosticException(entries);

        var result = DiagnosticTextRenderer.RenderCompilerDiagnostics(exception, "let", "test.ash");

        result.ShouldContain("test.ash:1:1");
        result.ShouldContain("Oops.");
    }

    [Test]
    public void RenderFailure_should_format_kind_and_message()
    {
        var result = DiagnosticTextRenderer.RenderFailure("error", "Something bad happened.");

        result.ShouldContain("error: Something bad happened.");
    }

    [Test]
    public void RenderFailure_should_include_display_path_when_provided()
    {
        var result = DiagnosticTextRenderer.RenderFailure("warning", "Watch out.", "file.ash");

        result.ShouldContain("warning: Watch out.");
        result.ShouldContain("--> file.ash");
    }

    [Test]
    public void RenderFailure_should_omit_display_path_when_null()
    {
        var result = DiagnosticTextRenderer.RenderFailure("error", "Bad.", null);

        result.ShouldContain("error: Bad.");
        result.ShouldNotContain("-->");
    }

    [Test]
    public void RenderFailure_should_omit_display_path_when_whitespace()
    {
        var result = DiagnosticTextRenderer.RenderFailure("error", "Bad.", "   ");

        result.ShouldNotContain("-->");
    }

    [Test]
    public void RenderCompilerDiagnostics_should_produce_minimum_one_caret_underline()
    {
        var entries = new[]
        {
            new DiagnosticEntry(TextSpan.FromBounds(2, 2), "Zero-length span.")
        };

        var result = DiagnosticTextRenderer.RenderCompilerDiagnostics(entries, "abcdef", "test.ash");

        result.ShouldContain("^");
    }
}
