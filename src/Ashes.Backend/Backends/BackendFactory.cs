namespace Ashes.Backend.Backends;

public static class BackendFactory
{
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
