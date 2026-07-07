using System.Formats.Tar;
using System.IO.Compression;
using Ashes.Registry.Storage;

namespace Ashes.Registry.Tests;

/// <summary>Builds gzip-compressed tarballs and an in-memory blob store for publish-side tests.</summary>
internal static class TestArchives
{
    public static byte[] Tarball(params (string Path, byte[] Bytes)[] files)
    {
        using var outer = new MemoryStream();
        using (var gzip = new GZipStream(outer, CompressionLevel.Optimal, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(bytes) });
            }
        }

        return outer.ToArray();
    }
}

/// <summary>An in-memory <see cref="IBlobStore"/> so pipeline tests need no filesystem.</summary>
internal sealed class InMemoryBlobStore : IBlobStore
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public int Count => _blobs.Count;

    public Task<bool> ExistsAsync(string hash, CancellationToken ct) => Task.FromResult(_blobs.ContainsKey(hash));

    public async Task PutAsync(string hash, Stream compressed, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await compressed.CopyToAsync(ms, ct);
        _blobs[hash] = ms.ToArray();
    }

    public Task<Stream?> OpenAsync(string hash, CancellationToken ct) =>
        Task.FromResult<Stream?>(_blobs.TryGetValue(hash, out var bytes) ? new MemoryStream(bytes) : null);
}
