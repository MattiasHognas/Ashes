using System.Collections.Concurrent;
using System.Reflection;
using Ashes.Backend.Backends;

namespace Ashes.Backend.Llvm;

internal static class HermeticTlsRuntimeAssets
{
    internal static string MbedTlsVersion { get; } = ResolveMbedTlsVersion();

    private static readonly ConcurrentDictionary<string, byte[]> Bitcode =
        new(StringComparer.Ordinal);

    internal static byte[] GetMbedTlsBitcode(string targetId)
    {
        return Bitcode.GetOrAdd(targetId, LoadMbedTlsBitcode);
    }

    private static byte[] LoadMbedTlsBitcode(string targetId)
    {
        TlsBitcodeAssetDescriptor descriptor = targetId switch
        {
            TargetIds.LinuxX64 => new TlsBitcodeAssetDescriptor(
                Path.Combine("linux-x64", "libmbedtls.bc"),
                Path.Combine("linux-x64", "mbedtls.version")),
            TargetIds.LinuxArm64 => new TlsBitcodeAssetDescriptor(
                Path.Combine("linux-arm64", "libmbedtls.bc"),
                Path.Combine("linux-arm64", "mbedtls.version")),
            TargetIds.WindowsX64 => new TlsBitcodeAssetDescriptor(
                Path.Combine("win-x64", "libmbedtls.bc"),
                Path.Combine("win-x64", "mbedtls.version")),
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unsupported hermetic TLS target '{targetId}'.")
        };

        string assetPath = ResolveRuntimeAssetPath(descriptor.RelativePath);
        ValidateProvisionedMbedTlsVersion(descriptor.VersionRelativePath);

        try
        {
            return File.ReadAllBytes(assetPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load Mbed TLS bitcode from '{assetPath}'.", ex);
        }
    }

    private static string ResolveRuntimeAssetPath(string relativePath)
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
            $"Missing Mbed TLS bitcode '{relativePath}'. Run scripts/download-mbedtls.sh to provision the TLS runtime payload.");
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

    private static void ValidateProvisionedMbedTlsVersion(string versionRelativePath)
    {
        string versionPath = ResolveRuntimeAssetPath(versionRelativePath);
        string provisionedVersion;

        try
        {
            provisionedVersion = File.ReadAllText(versionPath).Trim();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read hermetic TLS runtime version marker from '{versionPath}'.", ex);
        }

        if (!string.Equals(provisionedVersion, MbedTlsVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provisioned Mbed TLS version '{provisionedVersion}' does not match the compiler's expected version '{MbedTlsVersion}'. Re-run scripts/download-mbedtls.sh or update MbedTlsVersion.");
        }
    }

    private static string ResolveMbedTlsVersion()
    {
        Assembly backendAssembly = typeof(HermeticTlsRuntimeAssets).Assembly;
        foreach (AssemblyMetadataAttribute attribute in backendAssembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, "MbedTlsVersion", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(attribute.Value))
            {
                return attribute.Value;
            }
        }

        throw new InvalidOperationException("Missing MbedTlsVersion assembly metadata on Ashes.Backend.");
    }

    private readonly record struct TlsBitcodeAssetDescriptor(string RelativePath, string VersionRelativePath);
}
