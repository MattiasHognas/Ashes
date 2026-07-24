namespace Ashes.Formatter;

/// <summary>
/// The whitespace conventions the <see cref="Formatter"/> emits with: indent width, tabs versus
/// spaces, and line ending. Resolved from an <c>.editorconfig</c> chain by
/// <see cref="EditorConfigFormattingOptionsResolver"/>, or constructed directly for defaults.
/// </summary>
public readonly record struct FormattingOptions
{
    /// <summary>Creates options with the defaults: four-space indentation and LF line endings.</summary>
    public FormattingOptions()
    {
    }

    /// <summary>Number of columns per indentation level. Defaults to 4.</summary>
    public int IndentSize { get; init; } = 4;

    /// <summary>When true, indentation is emitted with tab characters instead of spaces.</summary>
    public bool UseTabs { get; init; }

    /// <summary>The line ending appended between lines, either <c>"\n"</c> or <c>"\r\n"</c>. Defaults to <c>"\n"</c>.</summary>
    public string NewLine { get; init; } = "\n";

    /// <summary>
    /// Returns a copy with any out-of-range values replaced by defaults: a non-positive
    /// <see cref="IndentSize"/> becomes 4, and a <see cref="NewLine"/> other than <c>"\n"</c> or
    /// <c>"\r\n"</c> becomes <c>"\n"</c>.
    /// </summary>
    public FormattingOptions Normalize()
    {
        var indentSize = IndentSize > 0 ? IndentSize : 4;
        var newLine = NewLine is "\n" or "\r\n" ? NewLine : "\n";
        return this with { IndentSize = indentSize, NewLine = newLine };
    }
}
