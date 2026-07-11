using System.Collections.Concurrent;
using System.Reflection;
using Ashes.Backend.Backends;

namespace Ashes.Backend.Llvm;

/// <summary>
/// Loads the vendored openlibm LLVM bitcode (<c>libopenlibm.bc</c>) that backs the Layer-2
/// transcendentals. The bitcode is linked into the program module at codegen time only when the
/// program actually calls a transcendental, mirroring how <see cref="HermeticTlsRuntimeAssets"/>
/// gates the Mbed TLS payload.
/// </summary>
internal static class HermeticMathRuntimeAssets
{
    private static readonly ConcurrentDictionary<string, byte[]> Bitcode = new(StringComparer.Ordinal);

    internal static byte[] GetOpenlibmBitcode(string targetId)
    {
        return Bitcode.GetOrAdd(targetId, LoadOpenlibmBitcode);
    }

    private static byte[] LoadOpenlibmBitcode(string targetId)
    {
        string rid = targetId switch
        {
            TargetIds.LinuxX64 => "linux-x64",
            TargetIds.LinuxArm64 => "linux-arm64",
            TargetIds.WindowsX64 => "win-x64",
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unsupported math runtime target '{targetId}'.")
        };

        string relativePath = Path.Combine(rid, "libopenlibm.bc");
        string assetPath = ResolveRuntimeAssetPath(relativePath);
        try
        {
            return File.ReadAllBytes(assetPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load openlibm bitcode from '{assetPath}'.", ex);
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
            $"Missing openlibm bitcode '{relativePath}'. Run scripts/download-openlibm.sh to provision the math runtime payload.");
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

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000:Avoid accessing Assembly file path when publishing as a single file",
        Justification = "An empty location is expected and handled by the caller; this only contributes an additional search root when not single-file published.")]
    private static string GetExecutingAssemblyLocation() => Assembly.GetExecutingAssembly().Location;
}
