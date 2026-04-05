using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class DiagnosticsInfrastructureTests
{
    [Test]
    public void TextSpan_FromBounds_should_normalize_negative_start()
    {
        var span = TextSpan.FromBounds(-5, 10);

        span.Start.ShouldBe(0);
        span.End.ShouldBe(10);
    }

    [Test]
    public void TextSpan_FromBounds_should_normalize_end_less_than_start()
    {
        var span = TextSpan.FromBounds(10, 5);

        span.Start.ShouldBe(10);
        span.End.ShouldBe(10);
    }

    [Test]
    public void TextSpan_FromStartLength_should_create_correct_span()
    {
        var span = TextSpan.FromStartLength(5, 3);

        span.Start.ShouldBe(5);
        span.End.ShouldBe(8);
        span.Length.ShouldBe(3);
    }

    [Test]
    public void TextSpan_FromStartLength_should_normalize_negative_length()
    {
        var span = TextSpan.FromStartLength(5, -3);

        span.Start.ShouldBe(5);
        span.End.ShouldBe(5);
        span.Length.ShouldBe(0);
    }

    [Test]
    public void TextSpan_Length_should_return_zero_when_start_equals_end()
    {
        var span = new TextSpan(5, 5);

        span.Length.ShouldBe(0);
    }

    [Test]
    public void TextSpan_Length_should_return_positive_value()
    {
        var span = new TextSpan(3, 10);

        span.Length.ShouldBe(7);
    }

    [Test]
    public void DiagnosticEntry_should_expose_span_properties()
    {
        var entry = new DiagnosticEntry(TextSpan.FromBounds(5, 10), "Test error.", "ASH001");

        entry.Start.ShouldBe(5);
        entry.End.ShouldBe(10);
        entry.Pos.ShouldBe(5);
        entry.Message.ShouldBe("Test error.");
        entry.Code.ShouldBe("ASH001");
    }

    [Test]
    public void DiagnosticEntry_code_should_default_to_null()
    {
        var entry = new DiagnosticEntry(TextSpan.FromBounds(0, 1), "Error.");

        entry.Code.ShouldBeNull();
    }

    [Test]
    public void Diagnostics_Error_with_single_pos_should_add_entry()
    {
        var diag = new Diagnostics();

        diag.Error(5, "Some error.");

        diag.StructuredErrors.Count.ShouldBe(1);
        diag.StructuredErrors[0].Start.ShouldBe(5);
        diag.StructuredErrors[0].End.ShouldBe(6);
        diag.StructuredErrors[0].Message.ShouldBe("Some error.");
    }

    [Test]
    public void Diagnostics_Error_with_pos_and_code_should_add_entry()
    {
        var diag = new Diagnostics();

        diag.Error(3, "Parse error.", "ASH003");

        diag.StructuredErrors.Count.ShouldBe(1);
        diag.StructuredErrors[0].Code.ShouldBe("ASH003");
    }

    [Test]
    public void Diagnostics_Error_with_start_end_should_add_entry()
    {
        var diag = new Diagnostics();

        diag.Error(2, 8, "Range error.");

        diag.StructuredErrors.Count.ShouldBe(1);
        diag.StructuredErrors[0].Start.ShouldBe(2);
        diag.StructuredErrors[0].End.ShouldBe(8);
    }

    [Test]
    public void Diagnostics_Error_with_start_end_and_code_should_add_entry()
    {
        var diag = new Diagnostics();

        diag.Error(0, 5, "Error.", "ASH001");

        diag.StructuredErrors[0].Code.ShouldBe("ASH001");
    }

    [Test]
    public void Diagnostics_Error_with_textspan_should_add_entry()
    {
        var diag = new Diagnostics();

        diag.Error(TextSpan.FromBounds(1, 4), "Span error.");

        diag.StructuredErrors.Count.ShouldBe(1);
        diag.StructuredErrors[0].Span.ShouldBe(TextSpan.FromBounds(1, 4));
    }

    [Test]
    public void Diagnostics_Error_with_textspan_and_code_should_add_entry()
    {
        var diag = new Diagnostics();

        diag.Error(TextSpan.FromBounds(1, 4), "Error.", "ASH002");

        diag.StructuredErrors[0].Code.ShouldBe("ASH002");
    }

    [Test]
    public void Diagnostics_Errors_should_format_with_position_prefix()
    {
        var diag = new Diagnostics();
        diag.Error(5, "Something went wrong.");

        diag.Errors.ShouldContain("[pos 5] Something went wrong.");
    }

    [Test]
    public void Diagnostics_ThrowIfAny_should_not_throw_when_empty()
    {
        var diag = new Diagnostics();

        Should.NotThrow(() => diag.ThrowIfAny());
    }

    [Test]
    public void Diagnostics_ThrowIfAny_should_throw_with_entries()
    {
        var diag = new Diagnostics();
        diag.Error(0, "First error.");
        diag.Error(5, "Second error.");

        var ex = Should.Throw<CompileDiagnosticException>(() => diag.ThrowIfAny());

        ex.StructuredErrors.Count.ShouldBe(2);
        ex.Message.ShouldContain("First error.");
        ex.Message.ShouldContain("Second error.");
    }

    [Test]
    public void Diagnostics_multiple_errors_should_accumulate()
    {
        var diag = new Diagnostics();
        diag.Error(0, "Error 1.");
        diag.Error(5, "Error 2.");
        diag.Error(10, "Error 3.");

        diag.StructuredErrors.Count.ShouldBe(3);
        diag.Errors.Count.ShouldBe(3);
    }

    [Test]
    public void CompileDiagnosticException_should_include_all_entries_in_message()
    {
        var entries = new[]
        {
            new DiagnosticEntry(TextSpan.FromBounds(0, 1), "Error A."),
            new DiagnosticEntry(TextSpan.FromBounds(5, 8), "Error B.")
        };

        var ex = new CompileDiagnosticException(entries);

        ex.Message.ShouldContain("[pos 0] Error A.");
        ex.Message.ShouldContain("[pos 5] Error B.");
        ex.StructuredErrors.Count.ShouldBe(2);
    }
}
