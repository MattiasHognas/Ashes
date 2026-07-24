using System.Formats.Tar;
using System.IO.Compression;
using Ashes.Registry.Api;
using Ashes.Registry.Storage;

namespace Ashes.Registry.Publish;

/// <summary>
/// Unpacks a gzip-compressed tarball into an in-memory source set while enforcing the publish limits —
/// per-file size, total uncompressed size (also the decompressed-size ceiling —
/// counted against bytes actually read, so a tiny upload cannot expand past the cap), file count, path
/// safety, and the source-only content allowlist. Any breach returns a <c>limit_exceeded</c> error and
/// nothing is stored.
/// </summary>
public static class SourceArchive
{
    /// <summary>Decompresses and unpacks the gzip tarball in <paramref name="gzip"/> into an in-memory
    /// source set, enforcing <paramref name="limits"/> and the path-safety and source-only rules. Returns
    /// the files on success, or a <c>limit_exceeded</c> error with nothing stored on any breach.</summary>
    public static async Task<(IReadOnlyList<SourceFile>? Files, PublishError? Error)> ExtractAsync(
        Stream gzip, RegistryLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gzip);
        ArgumentNullException.ThrowIfNull(limits);

        var files = new List<SourceFile>();
        long totalDecompressed = 0;

        await using var decompressed = new GZipStream(gzip, CompressionMode.Decompress);
        await using var reader = new TarReader(decompressed);

        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            var name = entry.Name;
            if (!IsPathSafe(name))
            {
                return Fail($"Unsafe path in archive: '{name}'.");
            }

            if (!IsAllowedContent(name))
            {
                return Fail($"Disallowed file type: '{name}'. Packages are source-only.");
            }

            if (files.Count + 1 > limits.MaxFileCount)
            {
                return Fail($"Too many files (limit {limits.MaxFileCount}).");
            }

            var entryStream = entry.DataStream;
            byte[] bytes;
            if (entryStream is null)
            {
                bytes = [];
            }
            else
            {
                using var buffer = new MemoryStream();
                var copied = await CopyGuardedAsync(entryStream, buffer, limits, totalDecompressed, ct);
                if (copied.Error is not null)
                {
                    return (null, copied.Error);
                }

                totalDecompressed += copied.Bytes;
                bytes = buffer.ToArray();
            }

            files.Add(new SourceFile(name, bytes));
        }

        return (files, null);
    }

    private static async Task<(long Bytes, PublishError? Error)> CopyGuardedAsync(
        Stream source, Stream dest, RegistryLimits limits, long totalSoFar, CancellationToken ct)
    {
        var rented = new byte[81920];
        long fileBytes = 0;
        int read;
        while ((read = await source.ReadAsync(rented, ct)) > 0)
        {
            fileBytes += read;
            if (fileBytes > limits.MaxFileBytes)
            {
                return (0, new PublishError(ErrorCodes.LimitExceeded, $"A file exceeds the {limits.MaxFileBytes}-byte limit."));
            }

            if (totalSoFar + fileBytes > limits.MaxTotalBytes)
            {
                return (0, new PublishError(ErrorCodes.LimitExceeded, $"Total source exceeds the {limits.MaxTotalBytes}-byte limit."));
            }

            await dest.WriteAsync(rented.AsMemory(0, read), ct);
        }

        return (fileBytes, null);
    }

    private static (IReadOnlyList<SourceFile>?, PublishError) Fail(string message) =>
        (null, new PublishError(ErrorCodes.LimitExceeded, message));

    private static bool IsPathSafe(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = name.Replace('\\', '/').Split('/');
        return !parts.Any(p => p is ".." or ".");
    }

    private static bool IsAllowedContent(string name)
    {
        var basename = name.Replace('\\', '/').Split('/')[^1].ToLowerInvariant();
        return basename.EndsWith(".ash", StringComparison.Ordinal)
            || basename is "ashes.json"
            || basename.StartsWith("readme", StringComparison.Ordinal)
            || basename.StartsWith("license", StringComparison.Ordinal);
    }
}
