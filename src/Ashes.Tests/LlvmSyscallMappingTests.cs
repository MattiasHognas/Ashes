using Ashes.Backend.Backends;
using Shouldly;
using System.Reflection;

namespace Ashes.Tests;

public sealed class LlvmSyscallMappingTests
{
    [Test]
    public void ResolveSyscallNr_should_map_linux_arm64_fcntl_and_epoll_syscalls()
    {
        Type codegenType = typeof(BackendFactory).Assembly.GetType("Ashes.Backend.Llvm.LlvmCodegen", throwOnError: true)!;
        Type flavorType = codegenType.GetNestedType("LlvmCodegenFlavor", BindingFlags.NonPublic)!;
        MethodInfo resolveMethod = codegenType.GetMethod("ResolveSyscallNr", BindingFlags.NonPublic | BindingFlags.Static)!;

        object linuxArm64 = Enum.Parse(flavorType, "LinuxArm64");

        ((long)resolveMethod.Invoke(null, [linuxArm64, 72L])!).ShouldBe(25L);
        ((long)resolveMethod.Invoke(null, [linuxArm64, 233L])!).ShouldBe(21L);
        ((long)resolveMethod.Invoke(null, [linuxArm64, 291L])!).ShouldBe(20L);
    }
}
