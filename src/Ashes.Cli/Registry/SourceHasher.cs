using System.Security.Cryptography;
using System.Text;

namespace Ashes.Cli.Registry;

/// <summary>
/// Client-side computation of the canonical <c>ash1:</c> source-tree hash: per
/// file emit <c>"&lt;sha256-hex&gt;  &lt;path&gt;\n"</c>, sort by path (ordinal), concatenate, SHA-256.
/// Must stay byte-identical to the server's computation so a publish's declared hash verifies.
/// </summary>
internal static class SourceHasher
{
    public const string Scheme = "ash1:";

    public static string Compute(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        var lines = new List<(string Path, string Line)>();
        foreach (var (path, bytes) in files)
        {
            var normalized = Normalize(path);
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            lines.Add((normalized, $"{digest}  {normalized}\n"));
        }

        lines.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var builder = new StringBuilder();
        foreach (var (_, line) in lines)
        {
            builder.Append(line);
        }

        return Scheme + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        return p.TrimStart('/');
    }
}
