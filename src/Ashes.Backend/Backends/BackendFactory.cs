namespace Ashes.Backend.Backends;

/// <summary>Resolves an <see cref="IBackend"/> for a given target RID and reports the default target
/// for the running operating system and architecture.</summary>
public static class BackendFactory
{
    /// <summary>
    /// Returns the <see cref="IBackend"/> that emits for <paramref name="targetId"/> (one of the
    /// values in <see cref="TargetIds"/>). Throws <see cref="ArgumentOutOfRangeException"/> for an
    /// unknown target.
    /// </summary>
    public static IBackend Create(string targetId)
    {
        return targetId switch
        {
            TargetIds.LinuxX64 => new LinuxX64LlvmBackend(),
            TargetIds.LinuxArm64 => new LinuxArm64LlvmBackend(),
            TargetIds.WindowsX64 => new WindowsX64LlvmBackend(),
            TargetIds.WindowsArm64 => new WindowsArm64LlvmBackend(),
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unknown target '{targetId}'.")
        };
    }

    /// <summary>
    /// Returns the target RID matching the host's operating system and architecture — the target used
    /// when the user does not pass <c>--target</c>. Throws <see cref="PlatformNotSupportedException"/>
    /// on an operating system that is neither Windows nor Linux.
    /// </summary>
    public static string DefaultForCurrentOS()
    {
        bool isArm64 = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64;

        if (OperatingSystem.IsWindows())
        {
            return isArm64 ? TargetIds.WindowsArm64 : TargetIds.WindowsX64;
        }

        if (OperatingSystem.IsLinux())
        {
            return isArm64 ? TargetIds.LinuxArm64 : TargetIds.LinuxX64;
        }

        throw new PlatformNotSupportedException(
            "The current operating system is not supported for a default backend target.");
    }
}
