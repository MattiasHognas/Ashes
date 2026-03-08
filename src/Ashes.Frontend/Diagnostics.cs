using System.Text;

namespace Ashes.Frontend;

public readonly record struct TextSpan(int Start, int End)
{
    public int Length => Math.Max(End - Start, 0);

    public static TextSpan FromBounds(int start, int end)
    {
        var normalizedStart = Math.Max(start, 0);
        var normalizedEnd = Math.Max(end, normalizedStart);
        return new TextSpan(normalizedStart, normalizedEnd);
    }

    public static TextSpan FromStartLength(int start, int length)
    {
        return FromBounds(start, start + Math.Max(length, 0));
    }
}

public static class DiagnosticCodes
{
    public const string UnknownIdentifier = "ASH001";
    public const string TypeMismatch = "ASH002";
    public const string ParseError = "ASH003";
    public const string MatchBranchTypeMismatch = "ASH004";
    public const string ListElementTypeMismatch = "ASH005";
}

public sealed record DiagnosticEntry(TextSpan Span, string Message, string? Code = null)
{
    public int Pos => Span.Start;
    public int Start => Span.Start;
    public int End => Span.End;
}

public sealed class CompileDiagnosticException(IReadOnlyList<DiagnosticEntry> errors)
    : InvalidOperationException(BuildMessage(errors))
{
    public IReadOnlyList<DiagnosticEntry> StructuredErrors { get; } = errors;

    private static string BuildMessage(IReadOnlyList<DiagnosticEntry> errors)
    {
        var sb = new StringBuilder();
        foreach (var e in errors)
        {
            sb.AppendLine($"[pos {e.Pos}] {e.Message}");
        }

        return sb.ToString();
    }
}

public sealed class Diagnostics
{
    private readonly List<DiagnosticEntry> _entries = new();

    public IReadOnlyList<string> Errors => _entries.Select(e => $"[pos {e.Pos}] {e.Message}").ToList();

    public IReadOnlyList<DiagnosticEntry> StructuredErrors => _entries;

    public void Error(int pos, string message)
    {
        Error(TextSpan.FromBounds(pos, pos + 1), message, null);
    }

    public void Error(int pos, string message, string? code)
    {
        Error(TextSpan.FromBounds(pos, pos + 1), message, code);
    }

    public void Error(int start, int end, string message)
    {
        Error(TextSpan.FromBounds(start, end), message, null);
    }

    public void Error(int start, int end, string message, string? code)
    {
        Error(TextSpan.FromBounds(start, end), message, code);
    }

    public void Error(TextSpan span, string message)
    {
        Error(span, message, null);
    }

    public void Error(TextSpan span, string message, string? code)
    {
        _entries.Add(new DiagnosticEntry(span, message, code));
    }

    public void ThrowIfAny()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        throw new CompileDiagnosticException(_entries);
    }
}
