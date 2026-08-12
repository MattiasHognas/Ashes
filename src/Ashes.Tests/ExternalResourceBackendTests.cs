using System.Buffers.Binary;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ExternalResourceBackendTests
{
    [Test]
    public void Declared_destructor_lowers_for_every_target_abi()
    {
        IrProgram linux = Lower("libresource.so");
        ReadElfMachine(new LinuxX64LlvmBackend().Compile(linux)).ShouldBe((ushort)62);
        ReadElfMachine(new LinuxArm64LlvmBackend().Compile(linux)).ShouldBe((ushort)183);

        IrProgram windows = Lower("resource.dll");
        ReadPeMachine(new WindowsX64LlvmBackend().Compile(windows)).ShouldBe((ushort)0x8664);
        ReadPeMachine(new WindowsArm64LlvmBackend().Compile(windows)).ShouldBe((ushort)0xAA64);
    }

    private static IrProgram Lower(string library)
    {
        string source = $$"""
            external type Handle resource destructor closeHandle
            external openHandle() -> Handle = "resource_open@{{library}}"
            external closeHandle(consume Handle) -> void = "resource_close@{{library}}"
            let resource = openHandle() in 0
            """;
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static ushort ReadElfMachine(byte[] image)
    {
        image.AsSpan(0, 4).ToArray().ShouldBe(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' });
        return BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(18, 2));
    }

    private static ushort ReadPeMachine(byte[] image)
    {
        image.AsSpan(0, 2).ToArray().ShouldBe(new byte[] { (byte)'M', (byte)'Z' });
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C, 4));
        return BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(peOffset + 4, 2));
    }
}
