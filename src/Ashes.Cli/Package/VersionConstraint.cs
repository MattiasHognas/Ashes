namespace Ashes.Cli.Package;

/// <summary>
/// A dependency version constraint: caret (<c>^1.2.0</c>, the default), tilde (<c>~1.2</c>), exact
/// (<c>=1.2.3</c>), or any (<c>*</c>). Internally a half-open range [lower, upper) plus an exact flag.
/// Prereleases only match when the constraint's own lower bound names one for the same core version.
/// </summary>
internal sealed class VersionConstraint
{
    private readonly SemVer? _lower;   // inclusive
    private readonly SemVer? _upper;   // exclusive
    private readonly bool _exact;

    private VersionConstraint(SemVer? lower, SemVer? upper, bool exact)
    {
        _lower = lower;
        _upper = upper;
        _exact = exact;
    }

    public string Raw { get; private init; } = "*";

    public static VersionConstraint Any { get; } = new(null, null, exact: false) { Raw = "*" };

    public static VersionConstraint Parse(string text)
    {
        var raw = (text ?? "*").Trim();
        if (raw is "*" or "")
        {
            return Any;
        }

        if (raw.StartsWith('=') && SemVer.TryParse(raw[1..], out var exact))
        {
            return new VersionConstraint(exact, null, exact: true) { Raw = raw };
        }

        if (raw.StartsWith('~'))
        {
            return Tilde(raw[1..], raw);
        }

        var body = raw.StartsWith('^') ? raw[1..] : raw; // bare version defaults to caret (Cargo model)
        return Caret(body, raw);
    }

    public bool Matches(SemVer version)
    {
        if (_lower is null)
        {
            return !version.IsPrerelease;
        }

        if (_exact)
        {
            return version.CompareTo(_lower) == 0;
        }

        if (version.CompareTo(_lower) < 0 || (_upper is not null && version.CompareTo(_upper) >= 0))
        {
            return false;
        }

        // Only admit a prerelease when the lower bound is a prerelease of the same core version.
        return !version.IsPrerelease
            || (_lower.IsPrerelease && SameCore(version, _lower));
    }

    public override string ToString() => Raw;

    private static VersionConstraint Caret(string body, string raw)
    {
        var lower = ParseLower(body);
        SemVer upper = (lower.Major, lower.Minor) switch
        {
            (0, 0) => new SemVer(0, 0, lower.Patch + 1, ""),
            (0, _) => new SemVer(0, lower.Minor + 1, 0, ""),
            _ => new SemVer(lower.Major + 1, 0, 0, ""),
        };
        return new VersionConstraint(lower, upper, exact: false) { Raw = raw };
    }

    private static VersionConstraint Tilde(string body, string raw)
    {
        var parts = body.Split('.');
        var lower = ParseLower(body);
        // ~1.2 / ~1.2.3 pin the minor; ~1 pins the major.
        var upper = parts.Length >= 2
            ? new SemVer(lower.Major, lower.Minor + 1, 0, "")
            : new SemVer(lower.Major + 1, 0, 0, "");
        return new VersionConstraint(lower, upper, exact: false) { Raw = raw };
    }

    private static SemVer ParseLower(string body)
    {
        var parts = body.Split('-')[0].Split('.');
        var major = ParseInt(parts.ElementAtOrDefault(0));
        var minor = ParseInt(parts.ElementAtOrDefault(1));
        var patch = ParseInt(parts.ElementAtOrDefault(2));
        var dash = body.IndexOf('-', StringComparison.Ordinal);
        var pre = dash >= 0 ? body[(dash + 1)..] : "";
        return new SemVer(major, minor, patch, pre);
    }

    private static int ParseInt(string? s) =>
        int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static bool SameCore(SemVer a, SemVer b) =>
        a.Major == b.Major && a.Minor == b.Minor && a.Patch == b.Patch;
}
