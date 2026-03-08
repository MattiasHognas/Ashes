namespace Ashes.Formatter;

public readonly record struct FormattingOptions
{
    public FormattingOptions()
    {
    }

    public int IndentSize { get; init; } = 4;

    public bool UseTabs { get; init; }

    public string NewLine { get; init; } = "\n";

    public FormattingOptions Normalize()
    {
        var indentSize = IndentSize > 0 ? IndentSize : 4;
        var newLine = NewLine is "\n" or "\r\n" ? NewLine : "\n";
        return this with { IndentSize = indentSize, NewLine = newLine };
    }
}
