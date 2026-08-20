using System.Text.Json;
using Ashes.Semantics;

namespace Ashes.Cli.Package;

/// <summary>
/// Decides whether an automatic registry restore is needed before a project command. Root overrides
/// provide the selected package's source directly, so their locked entries do not also need to exist
/// in the content-addressed cache.
/// </summary>
internal static class PackageRestorePolicy
{
    public static bool NeedsRestore(string manifestPath, LockFile? lockFile, PackageCache cache)
    {
        if (lockFile is null)
        {
            return true;
        }

        HashSet<string> overriddenNamespaces = ReadOverriddenNamespaces(manifestPath, lockFile.Package);
        return lockFile.Package.Any(package =>
            !overriddenNamespaces.Contains(package.Namespace) &&
            !cache.Has(package.Namespace, package.Version, package.Hash));
    }

    private static HashSet<string> ReadOverriddenNamespaces(
        string manifestPath,
        IReadOnlyList<LockedPackage> lockedPackages)
    {
        HashSet<string> lockedNamespaces = lockedPackages
            .Select(package => package.Namespace)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> result = new(StringComparer.Ordinal);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("overrides", out JsonElement overrides) ||
            overrides.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (JsonProperty entry in overrides.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object ||
                !entry.Value.TryGetProperty("path", out JsonElement path) ||
                path.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string expectedNamespace = lockedNamespaces.Contains(entry.Name)
                ? entry.Name
                : ProjectSupport.PascalCase(entry.Name);
            if (lockedNamespaces.Contains(expectedNamespace))
            {
                result.Add(expectedNamespace);
            }
        }

        return result;
    }
}
