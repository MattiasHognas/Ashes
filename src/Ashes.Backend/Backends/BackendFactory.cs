namespace Ashes.Backend.Backends;

public static class BackendFactory
{
    public static IBackend Create(string targetId)
    {
        return targetId switch
        {
            TargetIds.LinuxX64 => new LinuxX64ElfBackend(),
            TargetIds.WindowsX64 => new WindowsX64PeBackend(),
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unknown target '{targetId}'.")
        };
    }

    public static string DefaultForCurrentOS()
    {
        if (OperatingSystem.IsWindows())
        {
            return TargetIds.WindowsX64;
        }

        return TargetIds.LinuxX64;
    }
}
