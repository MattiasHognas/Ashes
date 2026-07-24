namespace Ashes.Backend.Backends;

/// <summary>The canonical runtime-identifier strings for the four supported targets, each of which is
/// both a compile target and a host RID on which a released compiler runs.</summary>
public static class TargetIds
{
    /// <summary>The 64-bit x86 Linux target RID (<c>linux-x64</c>).</summary>
    public const string LinuxX64 = "linux-x64";
    /// <summary>The 64-bit ARM Linux target RID (<c>linux-arm64</c>).</summary>
    public const string LinuxArm64 = "linux-arm64";
    /// <summary>The 64-bit x86 Windows target RID (<c>win-x64</c>).</summary>
    public const string WindowsX64 = "win-x64";
    /// <summary>The 64-bit ARM Windows target RID (<c>win-arm64</c>).</summary>
    public const string WindowsArm64 = "win-arm64";

    /// <summary>All four supported target RIDs, in the canonical order used across the toolchain.</summary>
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
