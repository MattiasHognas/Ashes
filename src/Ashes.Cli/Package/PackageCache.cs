using System.Formats.Tar;
using System.IO.Compression;
using Ashes.Semantics;

namespace Ashes.Cli.Package;

/// <summary>
/// The shared content-addressed cache of package source trees. The layout matches
/// <see cref="ProjectSupport.CachePathFor"/> so the compiler reads exactly what the CLI writes. Storing a
/// package unpacks its source tarball into <c>cache/pkg/&lt;ns&gt;/&lt;version&gt;/&lt;hashkey&gt;</c>,
/// atomically (temp dir + rename) and idempotently.
/// </summary>
internal sealed class PackageCache(string cacheRoot)
{
    public PackageCache()
        : this(ProjectSupport.PackageCacheRoot())
    {
    }

    public string PathFor(string ns, string version, string hash)
    {
        var colon = hash.IndexOf(':', StringComparison.Ordinal);
        var key = colon >= 0 ? hash[(colon + 1)..] : hash;
        return Path.Combine(cacheRoot, "pkg", ns, version, key);
    }

    public bool Has(string ns, string version, string hash) => Directory.Exists(PathFor(ns, version, hash));

    /// <summary>The <c>ash1:</c> content hash of a cached package's source tree — the same computation the
    /// registry ran at publish — so a restore can verify the cache against the lock (detecting corruption
    /// or a lying mirror).</summary>
    public static string ComputeTreeHash(string directory)
    {
        var files = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(directory, f), File.ReadAllBytes(f)));
        return Ashes.Cli.Registry.SourceHasher.Compute(files);
    }

    public async Task<string> StoreAsync(string ns, string version, string hash, byte[] gzipTarball, CancellationToken ct)
    {
        var dir = PathFor(ns, version, hash);
        if (Directory.Exists(dir))
        {
            return dir;
        }

        var temp = dir + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temp);
        try
        {
            await ExtractAsync(gzipTarball, temp, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            try
            {
                Directory.Move(temp, dir);
            }
            catch (IOException) when (Directory.Exists(dir))
            {
                // A concurrent restore won the race; its content is identical.
            }
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }

        return dir;
    }

    private static async Task ExtractAsync(byte[] gzipTarball, string destination, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        await using var gzip = new GZipStream(new MemoryStream(gzipTarball), CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);

        while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, entry.Name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue; // refuse a path escaping the cache directory
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var file = File.Create(target);
            if (entry.DataStream is { } data)
            {
                await data.CopyToAsync(file, ct).ConfigureAwait(false);
            }
        }
    }
}
