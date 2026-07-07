using System.Formats.Tar;
using System.IO.Compression;
using Ashes.Cli.Package;
using Shouldly;

namespace Ashes.Cli.Tests;

/// <summary>The lock file round-trip and the content-addressed cache's store/extract.</summary>
public sealed class CacheAndLockTests
{
    [Test]
    public void LockFile_round_trips()
    {
        var dir = TempDir();
        try
        {
            new LockFile
            {
                Version = 1,
                Package = [new LockedPackage("Json", "1.2.3", "registry+http://example", "ash1:abc", ["Utf8"])],
            }.Write(dir);

            var read = LockFile.Read(dir);

            read.ShouldNotBeNull();
            read.Version.ShouldBe(1);
            var p = read.Package.ShouldHaveSingleItem();
            p.Namespace.ShouldBe("Json");
            p.Version.ShouldBe("1.2.3");
            p.Hash.ShouldBe("ash1:abc");
            p.Dependencies.ShouldBe(["Utf8"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task PackageCache_stores_and_extracts_a_tarball()
    {
        var root = TempDir();
        try
        {
            var cache = new PackageCache(root);
            var tarball = Tarball(("ashes.json", "{}"u8.ToArray()), ("src/Foo.ash", "let x = 1\n"u8.ToArray()));

            cache.Has("Foo", "1.0.0", "ash1:deadbeef").ShouldBeFalse();
            var dir = await cache.StoreAsync("Foo", "1.0.0", "ash1:deadbeef", tarball, CancellationToken.None);

            cache.Has("Foo", "1.0.0", "ash1:deadbeef").ShouldBeTrue();
            File.Exists(Path.Combine(dir, "ashes.json")).ShouldBeTrue();
            File.Exists(Path.Combine(dir, "src", "Foo.ash")).ShouldBeTrue();

            // Storing again is an idempotent no-op returning the same directory.
            var again = await cache.StoreAsync("Foo", "1.0.0", "ash1:deadbeef", tarball, CancellationToken.None);
            again.ShouldBe(dir);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Tarball(params (string Path, byte[] Bytes)[] files)
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

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ashes-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
