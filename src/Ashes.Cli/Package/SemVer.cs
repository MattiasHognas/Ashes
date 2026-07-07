using System.Globalization;

namespace Ashes.Cli.Package;

/// <summary>A SemVer version: <c>major.minor.patch</c> with an optional prerelease tag, ordered by
/// SemVer precedence (a prerelease sorts below its release).</summary>
internal sealed record SemVer(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemVer>
{
    public bool IsPrerelease => Prerelease.Length > 0;

    public static bool TryParse(string text, out SemVer version)
    {
        version = new SemVer(0, 0, 0, "");
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim();
        var pre = "";
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = core[(dash + 1)..];
            core = core[..dash];
        }

        var parts = core.Split('.');
        if (parts.Length != 3 ||
            !TryInt(parts[0], out var major) || !TryInt(parts[1], out var minor) || !TryInt(parts[2], out var patch))
        {
            return false;
        }

        version = new SemVer(major, minor, patch, pre);
        return true;
    }

    public static SemVer Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Invalid SemVer version '{text}'.");

    public int CompareTo(SemVer? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }

        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
        {
            // A release outranks any prerelease of the same core; two releases are equal.
            return (Prerelease.Length == 0).CompareTo(other.Prerelease.Length == 0);
        }

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    public static bool operator <(SemVer? left, SemVer? right) => Comparer<SemVer>.Default.Compare(left, right) < 0;

    public static bool operator >(SemVer? left, SemVer? right) => Comparer<SemVer>.Default.Compare(left, right) > 0;

    public static bool operator <=(SemVer? left, SemVer? right) => Comparer<SemVer>.Default.Compare(left, right) <= 0;

    public static bool operator >=(SemVer? left, SemVer? right) => Comparer<SemVer>.Default.Compare(left, right) >= 0;

    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";

    private static int ComparePrerelease(string a, string b)
    {
        var left = a.Split('.');
        var right = b.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var numA = TryInt(left[i], out var na);
            var numB = TryInt(right[i], out var nb);
            int cmp;
            if (numA && numB)
            {
                cmp = na.CompareTo(nb);
            }
            else if (numA != numB)
            {
                cmp = numA ? -1 : 1; // numeric identifiers have lower precedence than alphanumeric
            }
            else
            {
                cmp = string.CompareOrdinal(left[i], right[i]);
            }

            if (cmp != 0)
            {
                return cmp;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool TryInt(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
