using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ashes.Cli.Registry;

/// <summary>
/// Client configuration for registries and credentials, stored under <c>~/.ashes</c>:
/// <c>config.json</c> maps registry names to base URLs (with an overridable <c>default</c>), and
/// <c>credentials.json</c> maps a registry base URL to its bearer token.
/// </summary>
internal static class RegistryConfig
{
#pragma warning disable S1075 // The canonical public instance is a deliberate baked-in default, overridable via config.
    public const string PublicInstanceUrl = "https://pkg.ashes-lang.org";
#pragma warning restore S1075

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string HomeDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ashes");

    private static string ConfigPath => Path.Combine(HomeDir, "config.json");

    private static string CredentialsPath => Path.Combine(HomeDir, "credentials.json");

    /// <summary>Resolve a registry name (null/"default" → the default entry, falling back to the public
    /// instance) to its base URL.</summary>
    public static string ResolveBaseUrl(string? registryName)
    {
        // A direct URL is accepted in place of a configured name (handy for CI and self-hosted servers).
        if (registryName is not null &&
            (registryName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             registryName.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return registryName.TrimEnd('/');
        }

        var config = Load<ConfigFile>(ConfigPath) ?? new ConfigFile();
        var name = string.IsNullOrEmpty(registryName) ? "default" : registryName;

        if (config.Registries.TryGetValue(name, out var url) && !string.IsNullOrWhiteSpace(url))
        {
            return url.TrimEnd('/');
        }

        if (string.Equals(name, "default", StringComparison.Ordinal))
        {
            return PublicInstanceUrl;
        }

        throw new CliUserException($"Unknown registry '{name}'. Add it to {ConfigPath} under \"registries\".");
    }

    public static string? GetToken(string baseUrl)
    {
        var creds = Load<Dictionary<string, string>>(CredentialsPath);
        return creds is not null && creds.TryGetValue(baseUrl, out var token) ? token : null;
    }

    public static void SetToken(string baseUrl, string token)
    {
        var creds = Load<Dictionary<string, string>>(CredentialsPath) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        creds[baseUrl] = token;
        Save(CredentialsPath, creds);
    }

    private static T? Load<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
    }

    private static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(HomeDir);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + Environment.NewLine);
    }

    private sealed class ConfigFile
    {
        public Dictionary<string, string> Registries { get; init; } = new(StringComparer.Ordinal);
    }
}
