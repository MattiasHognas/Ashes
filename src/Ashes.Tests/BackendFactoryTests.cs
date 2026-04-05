using Ashes.Backend.Backends;
using Shouldly;

namespace Ashes.Tests;

public sealed class BackendFactoryTests
{
    [Test]
    public void Create_should_return_linux_backend_for_linux_target()
    {
        var backend = BackendFactory.Create(TargetIds.LinuxX64);

        backend.ShouldBeOfType<LinuxX64LlvmBackend>();
    }

    [Test]
    public void Create_should_return_windows_backend_for_windows_target()
    {
        var backend = BackendFactory.Create(TargetIds.WindowsX64);

        backend.ShouldBeOfType<WindowsX64LlvmBackend>();
    }

    [Test]
    public void Create_should_throw_for_unknown_target()
    {
        var exception = Should.Throw<ArgumentOutOfRangeException>(() => BackendFactory.Create("unknown-target"));

        exception.ParamName.ShouldBe("targetId");
        exception.Message.ShouldContain("Unknown target 'unknown-target'.");
    }

    [Test]
    public void DefaultForCurrentOS_should_return_windows_target_on_windows_and_linux_target_otherwise()
    {
        var targetId = BackendFactory.DefaultForCurrentOS();

        targetId.ShouldBe(OperatingSystem.IsWindows() ? TargetIds.WindowsX64 : TargetIds.LinuxX64);
    }
}
