using System.Security.Cryptography;
using System.Text;

namespace Ashes.Registry.Publish;

/// <summary>
/// The canonical <c>ash1:</c> source-tree hash: per file emit
/// <c>"&lt;sha256-hex-of-bytes&gt;  &lt;path&gt;\n"</c>, sort those lines by path (ordinal), concatenate,
/// and SHA-256 the result. Only file paths and contents contribute — never directory entries, modes, or
/// ordering of the input.
/// </summary>
public static class ContentHash
{
    public const string Scheme = "ash1:";

    public static string Compute(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var lines = new List<(string Path, string Line)>();
        foreach (var file in files)
        {
            var path = Normalize(file.Path);
            var digest = Convert.ToHexStringLower(SHA256.HashData(file.Bytes));
            lines.Add((path, $"{digest}  {path}\n"));
        }

        lines.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var builder = new StringBuilder();
        foreach (var (_, line) in lines)
        {
            builder.Append(line);
        }

        var root = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        return Scheme + root;
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
