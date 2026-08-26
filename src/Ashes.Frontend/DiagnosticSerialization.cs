using System.Globalization;
using System.Text;

namespace Ashes.Frontend;

/// <summary>
/// Serializes a program parse's public diagnostic corpus into the versioned, implementation-neutral
/// parity format used by bootstrap tests. Text is represented as lowercase UTF-8 hex so every record
/// remains one line.
/// </summary>
public static class DiagnosticSerialization
{
    /// <summary>The schema marker written as the first line of every serialized diagnostic corpus.</summary>
    public const string Schema = "ashes-diagnostic-v1";

    /// <summary>
    /// Serializes <paramref name="recoveredItemCount"/> (the top-level items a parse recovered despite
    /// any diagnostics) followed by <paramref name="diagnostics"/> as tab-separated code, message hex,
    /// UTF-8 byte start, and UTF-8 byte length fields, in collection order. Every line, including the
    /// schema and the recovery summary, ends in a line-feed byte.
    /// </summary>
    public static string Serialize(int recoveredItemCount, IReadOnlyList<DiagnosticEntry> diagnostics)
    {
        var builder = new StringBuilder();
        builder.Append(Schema).Append('\n');
        builder.Append("recovered-items").Append('\t').Append(recoveredItemCount.ToString(CultureInfo.InvariantCulture)).Append('\n');

        foreach (DiagnosticEntry diagnostic in diagnostics)
        {
            builder.Append(diagnostic.Code ?? "-")
                .Append('\t')
                .Append(ToUtf8Hex(diagnostic.Message))
                .Append('\t')
                .Append(diagnostic.Start.ToString(CultureInfo.InvariantCulture))
                .Append('\t')
                .Append(diagnostic.Span.Length.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return builder.ToString();
    }

    private static string ToUtf8Hex(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte valueByte in bytes)
        {
            builder.Append(valueByte.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
