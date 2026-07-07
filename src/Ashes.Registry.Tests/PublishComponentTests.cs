using System.Text;
using Ashes.Registry.Publish;
using Ashes.Registry.Storage;
using Shouldly;

namespace Ashes.Registry.Tests;

/// <summary>Fast, DB-free unit tests for the publish building blocks (hash, archive limits, lint).</summary>
public sealed class PublishComponentTests
{
    [Test]
    public void ContentHash_is_deterministic_and_order_independent()
    {
        var a = new SourceFile("src/Json.ash", "one"u8.ToArray());
        var b = new SourceFile("src/Json/Parser.ash", "two"u8.ToArray());

        var h1 = ContentHash.Compute([a, b]);
        var h2 = ContentHash.Compute([b, a]);

        h1.ShouldBe(h2);
        h1.ShouldStartWith("ash1:");
    }

    [Test]
    public void ContentHash_changes_when_content_changes()
    {
        var before = ContentHash.Compute([new SourceFile("a.ash", "x"u8.ToArray())]);
        var after = ContentHash.Compute([new SourceFile("a.ash", "y"u8.ToArray())]);

        before.ShouldNotBe(after);
    }

    [Test]
    public async Task Archive_extracts_regular_files()
    {
        var tar = TestArchives.Tarball(
            ("src/Json.ash", "module"u8.ToArray()),
            ("ashes.json", "{}"u8.ToArray()));

        var (files, error) = await SourceArchive.ExtractAsync(new MemoryStream(tar), new RegistryLimits(), CancellationToken.None);

        error.ShouldBeNull();
        files!.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ShouldBe(["ashes.json", "src/Json.ash"]);
    }

    [Test]
    public async Task Archive_rejects_a_file_over_the_per_file_limit()
    {
        var tar = TestArchives.Tarball(("src/Big.ash", new byte[2048]));
        var limits = new RegistryLimits { MaxFileBytes = 1024 };

        var (files, error) = await SourceArchive.ExtractAsync(new MemoryStream(tar), limits, CancellationToken.None);

        files.ShouldBeNull();
        error!.Code.ShouldBe("limit_exceeded");
    }

    [Test]
    public async Task Archive_rejects_disallowed_content()
    {
        var tar = TestArchives.Tarball(("evil.exe", new byte[8]));

        var (files, error) = await SourceArchive.ExtractAsync(new MemoryStream(tar), new RegistryLimits(), CancellationToken.None);

        files.ShouldBeNull();
        error!.Code.ShouldBe("limit_exceeded");
    }

    [Test]
    public async Task Archive_rejects_path_traversal()
    {
        var tar = TestArchives.Tarball(("../escape.ash", new byte[8]));

        var (files, error) = await SourceArchive.ExtractAsync(new MemoryStream(tar), new RegistryLimits(), CancellationToken.None);

        files.ShouldBeNull();
        error!.Code.ShouldBe("limit_exceeded");
    }

    [Test]
    public void Namespace_lint_passes_modules_under_the_namespace()
    {
        var validator = new StructuralManifestValidator();
        var files = new[]
        {
            new SourceFile("src/Json.ash", Encoding.UTF8.GetBytes("x")),
            new SourceFile("src/Json/Parser.ash", Encoding.UTF8.GetBytes("y")),
            new SourceFile("README.md", Encoding.UTF8.GetBytes("z")),
        };

        validator.Validate(files, "Json").Ok.ShouldBeTrue();
    }

    [Test]
    public void Namespace_lint_fails_a_module_outside_the_namespace()
    {
        var validator = new StructuralManifestValidator();
        var files = new[] { new SourceFile("src/Other/Mod.ash", Encoding.UTF8.GetBytes("x")) };

        var result = validator.Validate(files, "Json");

        result.Ok.ShouldBeFalse();
        result.Message!.ShouldContain("Json");
    }
}
