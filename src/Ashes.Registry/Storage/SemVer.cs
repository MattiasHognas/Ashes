using System.Globalization;

namespace Ashes.Registry.Storage;

/// <summary>
/// Minimal SemVer comparison, sufficient for picking a package's "latest" display version. Full
/// constraint solving lives client-side in the resolver; this only orders concrete versions
/// (major.minor.patch with an optional prerelease tag, where a prerelease sorts below its release).
/// </summary>
internal static class SemVer
{
    public static bool TryParse(string value, out (int Major, int Minor, int Patch, string Pre) parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var core = value;
        var pre = "";
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            core = value[..dash];
            pre = value[(dash + 1)..];
        }

        var parts = core.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        parsed = (major, minor, patch, pre);
        return true;
    }

    public static int Compare(string a, string b)
    {
        var okA = TryParse(a, out var pa);
        var okB = TryParse(b, out var pb);

        // Unparseable versions sort below parseable ones, then lexically among themselves.
        if (!okA || !okB)
        {
            return okA == okB ? string.CompareOrdinal(a, b) : (okA ? 1 : -1);
        }

        var byCore = (pa.Major, pa.Minor, pa.Patch).CompareTo((pb.Major, pb.Minor, pb.Patch));
        if (byCore != 0)
        {
            return byCore;
        }

        // A release (no prerelease) outranks any prerelease of the same core.
        if (pa.Pre.Length == 0 || pb.Pre.Length == 0)
        {
            return (pb.Pre.Length == 0).CompareTo(pa.Pre.Length == 0);
        }

        return string.CompareOrdinal(pa.Pre, pb.Pre);
    }

    /// <summary>The highest non-yanked version by SemVer order, or null when there is none.</summary>
    public static string? Latest(IEnumerable<VersionInfo> versions)
    {
        string? best = null;
        foreach (var v in versions)
        {
            if (v.Yanked)
            {
                continue;
            }

            if (best is null || Compare(v.Version, best) > 0)
            {
                best = v.Version;
            }
        }

        return best;
    }
}
