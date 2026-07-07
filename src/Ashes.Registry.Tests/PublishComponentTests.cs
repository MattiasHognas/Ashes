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
    public void ContentHash_matches_the_pinned_spec_vector()
    {
        // Pinned ash1 for {("a.ash","x"),("b.ash","y")}. The CLI's SourceHasher
        // pins the SAME constant, so publish-time client/server hash agreement is locked in CI.
        ContentHash.Compute([new SourceFile("a.ash", "x"u8.ToArray()), new SourceFile("b.ash", "y"u8.ToArray())])
            .ShouldBe("ash1:c5c024c8b87b74fddfd0f8cd6d4728eca0324e051eb4f248613525a8158e3808");
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
        var validator = new SemanticManifestValidator();
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
        var validator = new SemanticManifestValidator();
        var files = new[] { new SourceFile("src/Other/Mod.ash", Encoding.UTF8.GetBytes("x")) };

        var result = validator.Validate(files, "Json");

        result.Ok.ShouldBeFalse();
        result.Message!.ShouldContain("Json");
    }

    [Test]
    public void Namespace_lint_reads_the_declared_source_roots()
    {
        var validator = new SemanticManifestValidator();
        var manifest = new SourceFile("ashes.json", """{ "name": "json", "sourceRoots": ["lib"] }"""u8.ToArray());

        // Under a non-`src` source root, the module is `Json` (not `lib.Json`).
        validator.Validate([manifest, new SourceFile("lib/Json.ash", "let x = 1\n"u8.ToArray())], "Json").Ok.ShouldBeTrue();

        // A module outside the namespace is still caught with a custom source root.
        validator.Validate([manifest, new SourceFile("lib/Widget.ash", "let x = 1\n"u8.ToArray())], "Json").Ok.ShouldBeFalse();
    }

    [Test]
    public void Namespace_lint_accepts_inline_modules_under_the_namespace()
    {
        var validator = new SemanticManifestValidator();
        var files = new[]
        {
            new SourceFile("src/Json.ash", "module Helpers =\n    let helper = given (n) -> n\n"u8.ToArray()),
        };

        // The inline module composes as `Json.Helpers`, under the namespace.
        validator.Validate(files, "Json").Ok.ShouldBeTrue();
    }
}
