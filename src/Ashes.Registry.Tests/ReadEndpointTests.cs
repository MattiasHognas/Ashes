using System.Net;
using System.Net.Http.Json;
using System.Text;
using Ashes.Registry.Api;
using Shouldly;

namespace Ashes.Registry.Tests;

/// <summary>The read HTTP surface over the in-memory host (REGISTRY_API §8, endpoint layer).</summary>
public sealed class ReadEndpointTests
{
    [Test]
    public async Task Healthz_reports_ok()
    {
        using var factory = new RegistryAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Index_reports_the_effective_limits()
    {
        using var factory = new RegistryAppFactory();
        using var client = factory.CreateClient();

        var index = await client.GetFromJsonAsync<IndexResponse>(new Uri("/api/v1/index", UriKind.Relative));

        index.ShouldNotBeNull();
        index.ApiVersion.ShouldBe("v1");
        index.Limits.MaxTotalBytes.ShouldBe(10L * 1024 * 1024);
    }

    [Test]
    public async Task Unknown_package_is_a_404_error_envelope()
    {
        using var factory = new RegistryAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/packages/DoesNotExist", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        body.ShouldNotBeNull();
        body.Error.Code.ShouldBe("not_found");
    }

    [Test]
    public async Task Package_endpoint_returns_metadata_and_versions()
    {
        using var factory = new RegistryAppFactory();
        await TestData.SeedAsync(
            factory,
            TestData.Package("Json", "A JSON parser.", keywords: ["json"], owners: ["alice"]),
            TestData.Version("Json", "1.2.0", "ash1:aaa", size: 20),
            Encoding.UTF8.GetBytes("source"));
        using var client = factory.CreateClient();

        var pkg = await client.GetFromJsonAsync<PackageResponse>(new Uri("/api/v1/packages/Json", UriKind.Relative));

        pkg.ShouldNotBeNull();
        pkg.Namespace.ShouldBe("Json");
        pkg.Owners.ShouldBe(["alice"]);
        pkg.Versions.Count.ShouldBe(1);
        pkg.Versions[0].Version.ShouldBe("1.2.0");
    }

    [Test]
    public async Task Version_endpoint_returns_one_version()
    {
        using var factory = new RegistryAppFactory();
        await TestData.SeedAsync(
            factory,
            TestData.Package("Json"),
            TestData.Version("Json", "1.2.0", "ash1:aaa"),
            Encoding.UTF8.GetBytes("source"));
        using var client = factory.CreateClient();

        var v = await client.GetFromJsonAsync<VersionResponse>(
            new Uri("/api/v1/packages/Json/1.2.0", UriKind.Relative));

        v.ShouldNotBeNull();
        v.Hash.ShouldBe("ash1:aaa");
    }

    [Test]
    public async Task Source_endpoint_streams_the_blob()
    {
        using var factory = new RegistryAppFactory();
        var source = Encoding.UTF8.GetBytes("the source tarball bytes");
        await TestData.SeedAsync(
            factory,
            TestData.Package("Json"),
            TestData.Version("Json", "1.2.0", "ash1:abc123", size: source.Length),
            source);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/packages/Json/1.2.0/source", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/gzip");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(source);
    }

    [Test]
    public async Task Search_finds_a_seeded_package()
    {
        using var factory = new RegistryAppFactory();
        await TestData.SeedAsync(
            factory,
            TestData.Package("Json", "A JSON parser."),
            TestData.Version("Json", "1.2.0", "ash1:aaa"),
            Encoding.UTF8.GetBytes("source"));
        using var client = factory.CreateClient();

        var results = await client.GetFromJsonAsync<SearchResponse>(
            new Uri("/api/v1/search?q=json", UriKind.Relative));

        results.ShouldNotBeNull();
        results.Results.ShouldContain(r => r.Namespace == "Json" && r.Latest == "1.2.0");
    }

    [Test]
    public async Task Browse_lists_seeded_packages()
    {
        using var factory = new RegistryAppFactory();
        await TestData.SeedAsync(
            factory,
            TestData.Package("Json"),
            TestData.Version("Json", "1.2.0", "ash1:aaa"),
            Encoding.UTF8.GetBytes("source"));
        using var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<BrowseResponse>(new Uri("/api/v1/packages", UriKind.Relative));

        page.ShouldNotBeNull();
        page.Packages.ShouldContain(p => p.Namespace == "Json");
    }
}
