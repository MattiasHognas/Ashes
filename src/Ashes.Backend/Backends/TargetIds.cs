namespace Ashes.Backend.Backends;

public static class TargetIds
{
    public const string LinuxX64 = "linux-x64";
    public const string LinuxArm64 = "linux-arm64";
    public const string WindowsX64 = "win-x64";
    public const string WindowsArm64 = "win-arm64";

    public static readonly string[] All = [LinuxX64, LinuxArm64, WindowsX64, WindowsArm64];

    /// <summary>True for the Windows PE targets (win-x64, win-arm64) — those that emit a
    /// <c>.exe</c> and run under the Windows/Wine execution model.</summary>
    public static bool IsWindows(string targetId) =>
        string.Equals(targetId, WindowsX64, StringComparison.Ordinal)
        || string.Equals(targetId, WindowsArm64, StringComparison.Ordinal);

    /// <summary>True for any known target id.</summary>
    public static bool IsKnown(string targetId) =>
        Array.Exists(All, id => string.Equals(id, targetId, StringComparison.Ordinal));
}
