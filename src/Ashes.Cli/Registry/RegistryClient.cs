using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Ashes.Cli.Registry;

/// <summary>Talks the registry HTTP API (REGISTRY_API §3). Maps the uniform error envelope onto
/// <see cref="CliUserException"/> so failures surface as ordinary CLI diagnostics.</summary>
internal sealed class RegistryClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(100) };

    public void Dispose() => _http.Dispose();

    public async Task<string> MintTokenAsync(string baseUrl, string accountName, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            Url(baseUrl, "/api/v1/tokens"), new { name = accountName }, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var token = await response.Content.ReadFromJsonAsync<TokenResponseDto>(Json, ct).ConfigureAwait(false);
        return token?.Token ?? throw new CliUserException("Registry returned no token.");
    }

    public async Task<SearchResponseDto> SearchAsync(string baseUrl, string query, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            Url(baseUrl, $"/api/v1/search?q={Uri.EscapeDataString(query)}"), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<SearchResponseDto>(Json, ct).ConfigureAwait(false)
            ?? new SearchResponseDto([], null);
    }

    public async Task<PackageResponseDto?> GetPackageAsync(string baseUrl, string ns, CancellationToken ct)
    {
        using var response = await _http.GetAsync(Url(baseUrl, $"/api/v1/packages/{ns}"), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PackageResponseDto>(Json, ct).ConfigureAwait(false);
    }

    public async Task PublishAsync(
        string baseUrl, string token, string ns, string version, string metadataJson, byte[] tarball, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(metadataJson, Encoding.UTF8), "metadata" },
        };
        var file = new ByteArrayContent(tarball);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        content.Add(file, "source", $"{ns}-{version}.tar.gz");

        using var request = new HttpRequestMessage(HttpMethod.Put, Url(baseUrl, $"/api/v1/packages/{ns}/{version}"))
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task SetYankAsync(
        string baseUrl, string token, string ns, string version, bool yanked, CancellationToken ct)
    {
        var verb = yanked ? "yank" : "unyank";
        using var request = new HttpRequestMessage(
            HttpMethod.Post, Url(baseUrl, $"/api/v1/packages/{ns}/{version}/{verb}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    private static Uri Url(string baseUrl, string path) => new(baseUrl.TrimEnd('/') + path);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? code = null;
        string? message = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<ErrorEnvelopeDto>(body, Json);
            code = envelope?.Error?.Code;
            message = envelope?.Error?.Message;
        }
        catch (JsonException)
        {
            // Non-JSON error body (proxy/HTML); fall back to the status line below.
        }

        var detail = message ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        throw new CliUserException(code is null ? $"Registry error: {detail}" : $"Registry error [{code}]: {detail}");
    }
}
