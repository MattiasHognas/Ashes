using System.Collections.Concurrent;
using System.Reflection;
using Ashes.Backend.Backends;

namespace Ashes.Backend.Llvm;

/// <summary>
/// Loads the vendored hermetic LLVM bitcode payloads that back the built-in runtimes: Mbed TLS
/// (Ashes.Net.Tls / Ashes.Net.Http), openlibm (Ashes.Number.Math transcendentals), and PCRE2 (Ashes.Text.Regex).
/// Each payload is linked into the program module at codegen time only when the program actually
/// uses the corresponding runtime ABI. Loading a payload validates the provisioned
/// <c>runtimes/&lt;rid&gt;/&lt;payload&gt;.version</c> marker against the version the compiler was
/// built for (assembly metadata stamped from <c>Directory.Build.props</c>), so a stale payload
/// fails fast instead of silently linking mismatched bitcode.
/// </summary>
internal sealed class HermeticRuntimeAssets
{
    internal static HermeticRuntimeAssets MbedTls { get; } = new(
        displayName: "Mbed TLS",
        bitcodeFileName: "libmbedtls.bc",
        versionFileName: "mbedtls.version",
        versionMetadataKey: "MbedTlsVersion",
        provisionScript: "scripts/download-mbedtls.sh");

    internal static HermeticRuntimeAssets Openlibm { get; } = new(
        displayName: "openlibm",
        bitcodeFileName: "libopenlibm.bc",
        versionFileName: "openlibm.version",
        versionMetadataKey: "OpenlibmVersion",
        provisionScript: "scripts/download-openlibm.sh");

    internal static HermeticRuntimeAssets Pcre2 { get; } = new(
        displayName: "PCRE2",
        bitcodeFileName: "libpcre2.bc",
        versionFileName: "pcre2.version",
        versionMetadataKey: "Pcre2Version",
        provisionScript: "scripts/download-pcre2.sh");

    private readonly string _displayName;
    private readonly string _bitcodeFileName;
    private readonly string _versionFileName;
    private readonly string _provisionScript;
    private readonly ConcurrentDictionary<string, byte[]> _bitcode = new(StringComparer.Ordinal);

    internal string ExpectedVersion { get; }

    private HermeticRuntimeAssets(
        string displayName,
        string bitcodeFileName,
        string versionFileName,
        string versionMetadataKey,
        string provisionScript)
    {
        _displayName = displayName;
        _bitcodeFileName = bitcodeFileName;
        _versionFileName = versionFileName;
        _provisionScript = provisionScript;
        ExpectedVersion = ResolveExpectedVersion(versionMetadataKey);
    }

    internal byte[] GetBitcode(string targetId)
    {
        return _bitcode.GetOrAdd(targetId, LoadBitcode);
    }

    private byte[] LoadBitcode(string targetId)
    {
        string rid = targetId switch
        {
            TargetIds.LinuxX64 => "linux-x64",
            TargetIds.LinuxArm64 => "linux-arm64",
            TargetIds.WindowsX64 => "win-x64",
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unsupported hermetic {_displayName} runtime target '{targetId}'.")
        };

        string assetPath = ResolveRuntimeAssetPath(Path.Combine(rid, _bitcodeFileName));
        ValidateProvisionedVersion(Path.Combine(rid, _versionFileName));

        try
        {
            return File.ReadAllBytes(assetPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load {_displayName} bitcode from '{assetPath}'.", ex);
        }
    }

    private string ResolveRuntimeAssetPath(string relativePath)
    {
        foreach (string runtimeRoot in EnumerateRuntimeRoots())
        {
            string candidate = Path.Combine(runtimeRoot, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Missing {_displayName} runtime asset '{relativePath}'. Run {_provisionScript} to provision the {_displayName} runtime payload.");
    }

    private void ValidateProvisionedVersion(string versionRelativePath)
    {
        string versionPath = ResolveRuntimeAssetPath(versionRelativePath);
        string provisionedVersion;

        try
        {
            provisionedVersion = File.ReadAllText(versionPath).Trim();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read hermetic {_displayName} runtime version marker from '{versionPath}'.", ex);
        }

        if (!string.Equals(provisionedVersion, ExpectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provisioned {_displayName} version '{provisionedVersion}' does not match the compiler's expected version '{ExpectedVersion}'. Re-run {_provisionScript} or update the version in Directory.Build.props.");
        }
    }

    private static IEnumerable<string> EnumerateRuntimeRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string start in EnumerateSearchStarts())
        {
            DirectoryInfo? current = new DirectoryInfo(start);
            while (current is not null)
            {
                string runtimeRoot = Path.Combine(current.FullName, "runtimes");
                if (Directory.Exists(runtimeRoot) && seen.Add(runtimeRoot))
                {
                    yield return runtimeRoot;
                }

                current = current.Parent;
            }
        }
    }

    private static IEnumerable<string> EnumerateSearchStarts()
    {
        yield return AppContext.BaseDirectory;

        string? assemblyDirectory = Path.GetDirectoryName(GetExecutingAssemblyLocation());
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            yield return assemblyDirectory;
        }

        yield return Directory.GetCurrentDirectory();
    }

    // Assembly.Location returns an empty string for assemblies embedded in a single-file app
    // (IL3000). That case is handled by the IsNullOrWhiteSpace check at the call site, where
    // AppContext.BaseDirectory and the current directory still cover lookup. Wrapping the call
    // in a helper lets us suppress IL3000 once for the intentional fallback.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000:Avoid accessing Assembly file path when publishing as a single file",
        Justification = "An empty location is expected and handled by the caller; this only contributes an additional search root when not single-file published.")]
    private static string GetExecutingAssemblyLocation() => Assembly.GetExecutingAssembly().Location;

    private static string ResolveExpectedVersion(string versionMetadataKey)
    {
        Assembly backendAssembly = typeof(HermeticRuntimeAssets).Assembly;
        foreach (AssemblyMetadataAttribute attribute in backendAssembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, versionMetadataKey, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(attribute.Value))
            {
                return attribute.Value;
            }
        }

        throw new InvalidOperationException($"Missing {versionMetadataKey} assembly metadata on Ashes.Backend.");
    }
}
