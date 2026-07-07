using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ashes.Registry.Api;
using Ashes.Registry.Publish;
using Shouldly;

namespace Ashes.Registry.Tests;

/// <summary>The authenticated write surface over the in-memory host.</summary>
public sealed class WriteEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly (string Path, byte[] Bytes)[] Source = [("src/Json.ash", "module Json"u8.ToArray())];

    // A valid single-module package whose exported `emit` needs the user-declared `Log` capability.
    private static readonly (string Path, byte[] Bytes)[] CapabilitySource =
    [
        ("src/Logger.ash", """
            capability Log =
                | write : Str -> Unit

            let emit : Str -> Unit needs {Log} =
                given (m) -> Log.write(m)
            """u8.ToArray()),
    ];

    [Test]
    public async Task Publishing_without_a_token_is_unauthorized()
    {
        using var factory = new RegistryAppFactory();
        using var client = factory.CreateClient();

        using var content = PublishBody(Source);
        var response = await client.PutAsync(new Uri("/api/v1/packages/Json/1.0.0", UriKind.Relative), content);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task A_publish_stores_the_version_and_claims_ownership()
    {
        using var factory = new RegistryAppFactory();
        using var client = factory.CreateClient();
        var token = await MintTokenAsync(client, "alice");

        var publish = await PublishAsync(client, token, "Json", "1.0.0", Source);
        publish.StatusCode.ShouldBe(HttpStatusCode.Created);

        var pkg = await client.GetFromJsonAsync<PackageResponse>(new Uri("/api/v1/packages/Json", UriKind.Relative));
        pkg!.Versions.ShouldContain(v => v.Version == "1.0.0");
        pkg.Owners.ShouldBe(["alice"]);
    }

    [Test]
    public async Task Publishing_to_a_namespace_owned_by_another_is_forbidden()
    {
        using var factory = new RegistryAppFactory();
        using var client = factory.CreateClient();
        var alice = await MintTokenAsync(client, "alice");
        (await PublishAsync(client, alice, "Json", "1.0.0", Source)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var bob = await MintTokenAsync(client, "bob");
        var response = await PublishAsync(client, bob, "Json", "2.0.0", Source);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        body!.Error.Code.ShouldBe("namespace_owned_by_another");
    }

    [Test]
    public async Task Yanking_a_version_flips_its_flag()
    {
        using var factory = new RegistryAppFactory();
        using var client = factory.CreateClient();
        var token = await MintTokenAsync(client, "alice");
        await PublishAsync(client, token, "Json", "1.0.0", Source);

        using var yank = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/packages/Json/1.0.0/yank", UriKind.Relative));
        yank.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.SendAsync(yank)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var v = await client.GetFromJsonAsync<VersionResponse>(new Uri("/api/v1/packages/Json/1.0.0", UriKind.Relative));
        v!.Yanked.ShouldBeTrue();
    }

    [Test]
    public async Task An_owner_can_add_a_co_owner()
    {
        using var factory = new RegistryAppFactory();
        using var client = factory.CreateClient();
        var alice = await MintTokenAsync(client, "alice");
        await MintTokenAsync(client, "bob"); // creates bob's account
        await PublishAsync(client, alice, "Json", "1.0.0", Source);

        using var add = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/packages/Json/owners", UriKind.Relative))
        {
            Content = JsonContent.Create(new { name = "bob" }),
        };
        add.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alice);
        var response = await client.SendAsync(add);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var owners = await response.Content.ReadFromJsonAsync<OwnersResponse>();
        owners!.Owners.ShouldBe(["alice", "bob"]);
    }

    [Test]
    public async Task A_published_package_records_its_inferred_capabilities()
    {
        using var factory = new RegistryAppFactory();
        using var client = factory.CreateClient();
        var token = await MintTokenAsync(client, "alice");

        (await PublishAsync(client, token, "Logger", "1.0.0", CapabilitySource))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var v = await client.GetFromJsonAsync<VersionResponse>(
            new Uri("/api/v1/packages/Logger/1.0.0", UriKind.Relative));
        v!.Capabilities.ShouldContain("Log");
    }

    private static async Task<string> MintTokenAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(new Uri("/api/v1/tokens", UriKind.Relative), new { name });
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return body!.Token;
    }

    private static async Task<HttpResponseMessage> PublishAsync(
        HttpClient client, string token, string ns, string version, (string Path, byte[] Bytes)[] files)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/packages/{ns}/{version}", UriKind.Relative))
        {
            Content = PublishBody(files),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static MultipartFormDataContent PublishBody((string Path, byte[] Bytes)[] files)
    {
        var meta = new PublishMetadataDto("A package.", ["json"], [], Hash(files));
        var content = new MultipartFormDataContent
        {
            { new StringContent(JsonSerializer.Serialize(meta, Web), Encoding.UTF8), "metadata" },
        };
        var file = new ByteArrayContent(TestArchives.Tarball(files));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        content.Add(file, "source", "source.tar.gz");
        return content;
    }

    private static string Hash((string Path, byte[] Bytes)[] files) =>
        ContentHash.Compute(files.Select(f => new SourceFile(f.Path, f.Bytes)).ToList());
}
