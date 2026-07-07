using Microsoft.Extensions.Options;

namespace Ashes.Registry.Storage;

/// <summary>
/// Content-addressed blob store on the local filesystem: <c>data/blobs/&lt;kk&gt;/&lt;key&gt;</c>, where
/// <c>key</c> is the hash with its <c>ash1:</c> scheme stripped (so the path is portable) and <c>kk</c> is
/// its first two characters for fan-out. Writes are atomic (temp file + rename) and idempotent, so a
/// content-addressed put is safe to repeat and safe under concurrency.
/// </summary>
internal sealed class FileSystemBlobStore : IBlobStore
{
    private readonly string _root;

    public FileSystemBlobStore(IOptions<RegistryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = Path.Combine(options.Value.DataDir, "blobs");
    }

    public Task<bool> ExistsAsync(string hash, CancellationToken ct) =>
        Task.FromResult(File.Exists(PathFor(hash)));

    public async Task PutAsync(string hash, Stream compressed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        var path = PathFor(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            return;
        }

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var dest = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await compressed.CopyToAsync(dest, ct);
            }

            File.Move(temp, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // A concurrent writer won the race; the content is identical, so this is success.
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public Task<Stream?> OpenAsync(string hash, CancellationToken ct)
    {
        var path = PathFor(hash);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
        return Task.FromResult(stream);
    }

    private string PathFor(string hash)
    {
        var key = Key(hash);
        var prefix = key.Length >= 2 ? key[..2] : "__";
        return Path.Combine(_root, prefix, key);
    }

    private static string Key(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        var colon = hash.IndexOf(':', StringComparison.Ordinal);
        var key = colon >= 0 ? hash[(colon + 1)..] : hash;
        foreach (var c in key)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                throw new ArgumentException($"Blob hash key must be hex; got '{hash}'.", nameof(hash));
            }
        }

        return key;
    }
}
