using Ashes.Cli.Registry;
using Shouldly;

namespace Ashes.Cli.Tests;

/// <summary>Pure client-side registry helpers (the live client/server flow is covered end-to-end).</summary>
public sealed class RegistryClientTests
{
    [Test]
    public void SourceHasher_matches_the_pinned_spec_vector()
    {
        // Same pinned ash1 the server's ContentHash test asserts: locks client/server hash parity in CI.
        var hash = SourceHasher.Compute([("a.ash", "x"u8.ToArray()), ("b.ash", "y"u8.ToArray())]);

        hash.ShouldBe("ash1:c5c024c8b87b74fddfd0f8cd6d4728eca0324e051eb4f248613525a8158e3808");
    }

    [Test]
    public void SourceHasher_is_order_independent_and_normalizes_paths()
    {
        var forward = SourceHasher.Compute([("a.ash", "x"u8.ToArray()), ("b.ash", "y"u8.ToArray())]);
        var reversedAndPrefixed = SourceHasher.Compute([("./b.ash", "y"u8.ToArray()), ("a.ash", "x"u8.ToArray())]);

        reversedAndPrefixed.ShouldBe(forward);
    }

    [Test]
    public void DeriveNamespace_pascal_cases_the_name_when_none_is_explicit()
    {
        ProjectPackager.DeriveNamespace(null, "json-parser").ShouldBe("JsonParser");
        ProjectPackager.DeriveNamespace(null, "widget").ShouldBe("Widget");
        ProjectPackager.DeriveNamespace("MyNs", "whatever").ShouldBe("MyNs");
    }

    [Test]
    public void ArgScanner_separates_options_flags_and_positionals()
    {
        var scanned = ArgScanner.Parse(["Widget", "1.0.0", "--registry", "acme", "--undo"]);

        scanned.Positionals.ShouldBe(["Widget", "1.0.0"]);
        scanned.Value("registry").ShouldBe("acme");
        scanned.Flag("undo").ShouldBeTrue();
        scanned.Flag("missing").ShouldBeFalse();
    }

    [Test]
    public void ArgScanner_rejects_a_dangling_option()
    {
        Should.Throw<CliUsageException>(() => ArgScanner.Parse(["--registry"]));
    }

    [Test]
    public void ResolveBaseUrl_accepts_a_direct_url_without_touching_config()
    {
        // A direct URL short-circuits before any config lookup, so this stays hermetic.
        RegistryConfig.ResolveBaseUrl("http://127.0.0.1:5211/").ShouldBe("http://127.0.0.1:5211");
        RegistryConfig.ResolveBaseUrl("https://pkg.example.com").ShouldBe("https://pkg.example.com");
    }

    [Test]
    public void Manifest_reads_publish_fields_and_only_registry_dependencies()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path,
            """
            {
              "name": "widget",
              "namespace": "Widget",
              "version": "1.2.3",
              "description": "A widget library.",
              "keywords": ["widget", "draw"],
              "dependencies": {
                "json": "^1.0",
                "local": { "path": "../local" }
              }
            }
            """);
        try
        {
            var manifest = Manifest.Read(path);

            manifest.Namespace.ShouldBe("Widget");
            manifest.Version.ShouldBe("1.2.3");
            manifest.Description.ShouldBe("A widget library.");
            manifest.Keywords.ShouldBe(["widget", "draw"]);
            manifest.Dependencies.Count.ShouldBe(1); // the path dependency is not published metadata
            manifest.Dependencies[0].Namespace.ShouldBe("json");
            manifest.Dependencies[0].Req.ShouldBe("^1.0");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
