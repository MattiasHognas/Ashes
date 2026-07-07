using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Ashes.Semantics;

namespace Ashes.Cli.Registry;

/// <summary>Packages a loaded project's source into the source-only upload the registry expects: the
/// <c>.ash</c> modules under the source roots plus a few root metadata files.</summary>
internal static class ProjectPackager
{
    public static IReadOnlyList<(string Path, byte[] Bytes)> GatherFiles(AshesProject project)
    {
        var root = project.ProjectDirectory;
        var files = new List<(string Path, byte[] Bytes)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourceRoot in project.SourceRoots)
        {
            var dir = Path.GetFullPath(Path.Combine(root, sourceRoot));
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.ash", SearchOption.AllDirectories))
            {
                Add(files, seen, root, file);
            }
        }

        foreach (var name in Directory.EnumerateFiles(root))
        {
            var basename = Path.GetFileName(name);
            var lower = basename.ToLowerInvariant();
            if (lower is "ashes.json" || lower.StartsWith("readme", StringComparison.Ordinal) || lower.StartsWith("license", StringComparison.Ordinal))
            {
                Add(files, seen, root, name);
            }
        }

        return files;
    }

    public static byte[] BuildTarball(IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        using var outer = new MemoryStream();
        using (var gzip = new GZipStream(outer, CompressionLevel.Optimal, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, bytes) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(bytes) });
            }
        }

        return outer.ToArray();
    }

    /// <summary>The publishing namespace: an explicit <c>namespace</c> field wins, else the package name
    /// mapped to PascalCase.</summary>
    public static string DeriveNamespace(string? explicitNamespace, string? name)
    {
        if (!string.IsNullOrWhiteSpace(explicitNamespace))
        {
            return explicitNamespace;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CliUserException("Cannot determine a namespace: set \"name\" or \"namespace\" in ashes.json.");
        }

        var builder = new StringBuilder();
        var capitalizeNext = true;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c))
            {
                capitalizeNext = true;
                continue;
            }

            builder.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
            capitalizeNext = false;
        }

        return builder.Length == 0
            ? throw new CliUserException($"Cannot derive a namespace from name '{name}'.")
            : builder.ToString();
    }

    private static void Add(
        List<(string Path, byte[] Bytes)> files, HashSet<string> seen, string root, string absolutePath)
    {
        var relative = Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
        if (seen.Add(relative))
        {
            files.Add((relative, File.ReadAllBytes(absolutePath)));
        }
    }
}
