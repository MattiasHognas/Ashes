using Ashes.Backend.Backends;
using Shouldly;

namespace Ashes.Tests;

public sealed class BackendFactoryEdgeCaseTests
{
    [Test]
    public void Create_should_return_linux_arm64_backend_for_arm64_target()
    {
        var backend = BackendFactory.Create(TargetIds.LinuxArm64);

        backend.ShouldBeOfType<LinuxArm64LlvmBackend>();
    }

    [Test]
    public void Create_should_throw_for_empty_target_id()
    {
        var exception = Should.Throw<ArgumentOutOfRangeException>(() => BackendFactory.Create(""));

        exception.ParamName.ShouldBe("targetId");
    }

    [Test]
    public void Create_should_throw_for_case_mismatch_target()
    {
        // Target IDs are case-sensitive
        Should.Throw<ArgumentOutOfRangeException>(() => BackendFactory.Create("Linux-X64"));
    }

    [Test]
    public void TargetIds_should_have_expected_values()
    {
        TargetIds.LinuxX64.ShouldBe("linux-x64");
        TargetIds.LinuxArm64.ShouldBe("linux-arm64");
        TargetIds.WindowsX64.ShouldBe("win-x64");
    }

    [Test]
    public void DefaultForCurrentOS_should_return_valid_target_id()
    {
        var targetId = BackendFactory.DefaultForCurrentOS();

        // Should be one of the known target IDs
        var knownIds = new[] { TargetIds.LinuxX64, TargetIds.LinuxArm64, TargetIds.WindowsX64 };
        knownIds.ShouldContain(targetId);
    }

    [Test]
    public void Create_should_return_distinct_backends_for_all_targets()
    {
        var linuxX64 = BackendFactory.Create(TargetIds.LinuxX64);
        var linuxArm64 = BackendFactory.Create(TargetIds.LinuxArm64);
        var windowsX64 = BackendFactory.Create(TargetIds.WindowsX64);

        linuxX64.GetType().ShouldNotBe(linuxArm64.GetType());
        linuxX64.GetType().ShouldNotBe(windowsX64.GetType());
        linuxArm64.GetType().ShouldNotBe(windowsX64.GetType());
    }
}
