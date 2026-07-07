using System.Collections.Concurrent;
using System.Reflection;
using Ashes.Backend.Backends;

namespace Ashes.Backend.Llvm;

/// <summary>
/// Loads the vendored PCRE2 8-bit LLVM bitcode (<c>libpcre2.bc</c>) that backs Ashes.Regex. The
/// bitcode is linked into the program module at codegen time only when the program actually uses a
/// regex intrinsic (<see cref="LlvmCodegen.ProgramUsesRegexRuntimeAbi"/>), mirroring how
/// <see cref="HermeticMathRuntimeAssets"/> gates the openlibm payload.
/// </summary>
internal static class HermeticRegexRuntimeAssets
{
    private static readonly ConcurrentDictionary<string, byte[]> Bitcode = new(StringComparer.Ordinal);

    internal static byte[] GetPcre2Bitcode(string targetId)
    {
        return Bitcode.GetOrAdd(targetId, LoadPcre2Bitcode);
    }

    private static byte[] LoadPcre2Bitcode(string targetId)
    {
        string rid = targetId switch
        {
            TargetIds.LinuxX64 => "linux-x64",
            TargetIds.LinuxArm64 => "linux-arm64",
            TargetIds.WindowsX64 => "win-x64",
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unsupported regex runtime target '{targetId}'.")
        };

        string relativePath = Path.Combine(rid, "libpcre2.bc");
        string assetPath = ResolveRuntimeAssetPath(relativePath);
        try
        {
            return File.ReadAllBytes(assetPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load PCRE2 bitcode from '{assetPath}'.", ex);
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
            $"Missing PCRE2 bitcode '{relativePath}'. Run scripts/download-pcre2.sh to provision the regex runtime payload.");
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
