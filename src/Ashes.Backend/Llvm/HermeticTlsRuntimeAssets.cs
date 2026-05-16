using System.Collections.Concurrent;
using System.Reflection;
using Ashes.Backend.Backends;

namespace Ashes.Backend.Llvm;

internal static class HermeticTlsRuntimeAssets
{
    internal static string RustlsVersion { get; } = ResolveRustlsVersion();

    private static readonly ConcurrentDictionary<string, HermeticTlsRuntimeAsset> RustlsSharedLibraries =
        new(StringComparer.Ordinal);

    internal static HermeticTlsRuntimeAsset GetRustlsSharedLibrary(string targetId)
    {
        return RustlsSharedLibraries.GetOrAdd(targetId, LoadRustlsSharedLibrary);
    }

    private static HermeticTlsRuntimeAsset LoadRustlsSharedLibrary(string targetId)
    {
        RustlsAssetDescriptor descriptor = targetId switch
        {
            TargetIds.LinuxX64 => new RustlsAssetDescriptor(
                Path.Combine("linux-x64", "librustls.so"),
                Path.Combine("linux-x64", "rustls.version"),
                $"librustls-{RustlsVersion}.so"),
            TargetIds.LinuxArm64 => new RustlsAssetDescriptor(
                Path.Combine("linux-arm64", "librustls.so"),
                Path.Combine("linux-arm64", "rustls.version"),
                $"librustls-{RustlsVersion}-arm64.so"),
            TargetIds.WindowsX64 => new RustlsAssetDescriptor(
                Path.Combine("win-x64", "rustls.dll"),
                Path.Combine("win-x64", "rustls.version"),
                $"rustls-{RustlsVersion}.dll"),
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unsupported hermetic TLS target '{targetId}'.")
        };

        string assetPath = ResolveRuntimeAssetPath(descriptor.RelativePath);
        ValidateProvisionedRustlsVersion(descriptor.VersionRelativePath);

        try
        {
            return new HermeticTlsRuntimeAsset(descriptor.EmbeddedFileName, File.ReadAllBytes(assetPath));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load hermetic TLS runtime asset from '{assetPath}'.", ex);
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
            $"Missing hermetic TLS runtime asset '{relativePath}'. Run scripts/download-rustls-ffi.sh to provision rustls-ffi payloads.");
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

        string? assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            yield return assemblyDirectory;
        }

        yield return Directory.GetCurrentDirectory();
    }

    private static void ValidateProvisionedRustlsVersion(string versionRelativePath)
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

        if (!string.Equals(provisionedVersion, RustlsVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provisioned hermetic TLS runtime version '{provisionedVersion}' does not match the compiler's expected rustls-ffi version '{RustlsVersion}'. Re-run scripts/download-rustls-ffi.sh or update RustlsFfiVersion.");
        }
    }

    private static string ResolveRustlsVersion()
    {
        Assembly backendAssembly = typeof(HermeticTlsRuntimeAssets).Assembly;
        foreach (AssemblyMetadataAttribute attribute in backendAssembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, "RustlsFfiVersion", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(attribute.Value))
            {
                return attribute.Value;
            }
        }

        throw new InvalidOperationException("Missing RustlsFfiVersion assembly metadata on Ashes.Backend.");
    }

    private readonly record struct RustlsAssetDescriptor(string RelativePath, string VersionRelativePath, string EmbeddedFileName);
}

internal sealed record HermeticTlsRuntimeAsset(string EmbeddedFileName, byte[] Bytes);