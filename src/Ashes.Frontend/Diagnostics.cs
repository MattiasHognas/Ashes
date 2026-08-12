using System.Text;

namespace Ashes.Frontend;

/// <summary>
/// A half-open range of character offsets into a source string, used to locate diagnostics and AST
/// nodes. Constructed via <see cref="FromBounds(int, int)"/> or <see cref="FromStartLength(int, int)"/>,
/// which normalize negative or inverted inputs.
/// </summary>
/// <param name="Start">Inclusive start offset (0-based).</param>
/// <param name="End">Exclusive end offset; never less than <paramref name="Start"/> once normalized.</param>
public readonly record struct TextSpan(int Start, int End)
{
    /// <summary>Number of characters covered, clamped to zero for an empty or inverted span.</summary>
    public int Length => Math.Max(End - Start, 0);

    /// <summary>Builds a span from explicit bounds, clamping <paramref name="start"/> to zero and
    /// <paramref name="end"/> to at least the normalized start.</summary>
    public static TextSpan FromBounds(int start, int end)
    {
        var normalizedStart = Math.Max(start, 0);
        var normalizedEnd = Math.Max(end, normalizedStart);
        return new TextSpan(normalizedStart, normalizedEnd);
    }

    /// <summary>Builds a span from a start offset and a length, clamping a negative
    /// <paramref name="length"/> to zero.</summary>
    public static TextSpan FromStartLength(int start, int length)
    {
        return FromBounds(start, start + Math.Max(length, 0));
    }
}

/// <summary>
/// The stable <c>ASHnnn</c> string codes attached to compiler diagnostics, used for cross-referencing
/// with the diagnostics reference and for machine-readable filtering.
/// </summary>
public static class DiagnosticCodes
{
    /// <summary>A referenced name is not bound in scope.</summary>
    public const string UnknownIdentifier = "ASH001";
    /// <summary>An expression's inferred type conflicts with the type required by its context.</summary>
    public const string TypeMismatch = "ASH002";
    /// <summary>The source is syntactically malformed (lexer or parser error).</summary>
    public const string ParseError = "ASH003";
    /// <summary>Two arms of a <c>match</c> produce incompatible result types.</summary>
    public const string MatchBranchTypeMismatch = "ASH004";
    /// <summary>Elements of a list literal do not all share one type.</summary>
    public const string ListElementTypeMismatch = "ASH005";
    /// <summary>A value is used after it has already been dropped.</summary>
    public const string UseAfterDrop = "ASH006";
    /// <summary>A value is dropped more than once.</summary>
    public const string DoubleDrop = "ASH007";
    /// <summary>A value is used after ownership of it has been moved elsewhere.</summary>
    public const string UseAfterMove = "ASH008";
    /// <summary>A trait declaration, method signature, constraint, or deriving clause is invalid.</summary>
    public const string InvalidTraitDeclaration = "ASH010";
    /// <summary>A trait implementation declaration or generated derived implementation is invalid.</summary>
    public const string InvalidTraitImplementation = "ASH011";
    /// <summary>Trait coherence is violated by overlap, an orphan implementation, or ambiguous evidence.</summary>
    public const string TraitCoherence = "ASH012";
    /// <summary>A trait, default-method, or implementation-requirement cycle cannot terminate.</summary>
    public const string TraitCycle = "ASH025";
    /// <summary>A concrete trait requirement has no resolvable implementation.</summary>
    public const string TraitResolution = "ASH036";
    /// <summary>A transparent type-alias expansion contains a direct or indirect cycle.</summary>
    public const string RecursiveTypeAlias = "ASH039";
    /// <summary>A zero-cost type declaration does not have exactly one constructor payload.</summary>
    public const string InvalidZeroCostTypeDeclaration = "ASH040";
    /// <summary>An external resource names a missing or invalid destructor declaration.</summary>
    public const string InvalidExternalResourceDestructor = "ASH041";
    /// <summary>An external parameter has a missing or invalid resource ownership marker.</summary>
    public const string InvalidExternalOwnershipMarker = "ASH042";
}

/// <summary>
/// A single compiler diagnostic: a source <paramref name="Span"/>, a human-readable
/// <paramref name="Message"/>, and an optional <paramref name="Code"/> from <see cref="DiagnosticCodes"/>.
/// </summary>
/// <param name="Span">The source range the diagnostic points at.</param>
/// <param name="Message">The rendered, user-facing message text.</param>
/// <param name="Code">The stable <c>ASHnnn</c> code, or null for an uncoded diagnostic.</param>
public sealed record DiagnosticEntry(TextSpan Span, string Message, string? Code = null)
{
    /// <summary>The start offset of <see cref="Span"/>, used as the diagnostic's primary position.</summary>
    public int Pos => Span.Start;
    /// <summary>Inclusive start offset of <see cref="Span"/>.</summary>
    public int Start => Span.Start;
    /// <summary>Exclusive end offset of <see cref="Span"/>.</summary>
    public int End => Span.End;
}

/// <summary>
/// Thrown to abort compilation when one or more diagnostics have been collected. The individual
/// entries are preserved in <see cref="StructuredErrors"/> for structured rendering, and the exception
/// message flattens them into positioned text.
/// </summary>
public sealed class CompileDiagnosticException(IReadOnlyList<DiagnosticEntry> errors)
    : InvalidOperationException(BuildMessage(errors))
{
    /// <summary>The diagnostics that caused compilation to fail, in the order they were collected.</summary>
    public IReadOnlyList<DiagnosticEntry> StructuredErrors { get; } = errors;

    private static string BuildMessage(IReadOnlyList<DiagnosticEntry> errors)
    {
        var sb = new StringBuilder();
        foreach (var e in errors)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"[pos {e.Pos}] {e.Message}");
        }

        return sb.ToString();
    }
}

/// <summary>
/// A collecting sink for compiler diagnostics. Phases report errors here as they run rather than
/// throwing, then a caller invokes <see cref="ThrowIfAny"/> to surface everything at once as a
/// <see cref="CompileDiagnosticException"/>.
/// </summary>
public sealed class Diagnostics
{
    private readonly List<DiagnosticEntry> _entries = new();

    /// <summary>The collected diagnostics rendered as positioned message strings.</summary>
    public IReadOnlyList<string> Errors => _entries.Select(e => $"[pos {e.Pos}] {e.Message}").ToList();

    /// <summary>The collected diagnostics as structured entries, in collection order.</summary>
    public IReadOnlyList<DiagnosticEntry> StructuredErrors => _entries;

    /// <summary>Records an uncoded error spanning a single character at <paramref name="pos"/>.</summary>
    public void Error(int pos, string message)
    {
        Error(TextSpan.FromBounds(pos, pos + 1), message, null);
    }

    /// <summary>Records an error with <paramref name="code"/> spanning a single character at
    /// <paramref name="pos"/>.</summary>
    public void Error(int pos, string message, string? code)
    {
        Error(TextSpan.FromBounds(pos, pos + 1), message, code);
    }

    /// <summary>Records an uncoded error spanning <paramref name="start"/> to <paramref name="end"/>.</summary>
    public void Error(int start, int end, string message)
    {
        Error(TextSpan.FromBounds(start, end), message, null);
    }

    /// <summary>Records an error with <paramref name="code"/> spanning <paramref name="start"/> to
    /// <paramref name="end"/>.</summary>
    public void Error(int start, int end, string message, string? code)
    {
        Error(TextSpan.FromBounds(start, end), message, code);
    }

    /// <summary>Records an uncoded error over <paramref name="span"/>.</summary>
    public void Error(TextSpan span, string message)
    {
        Error(span, message, null);
    }

    /// <summary>Records an error with <paramref name="code"/> over <paramref name="span"/>; the
    /// underlying sink all other overloads funnel into.</summary>
    public void Error(TextSpan span, string message, string? code)
    {
        _entries.Add(new DiagnosticEntry(span, message, code));
    }

    /// <summary>Throws a <see cref="CompileDiagnosticException"/> carrying every collected diagnostic
    /// when at least one has been recorded; otherwise returns without effect.</summary>
    public void ThrowIfAny()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        throw new CompileDiagnosticException(_entries);
    }
}
