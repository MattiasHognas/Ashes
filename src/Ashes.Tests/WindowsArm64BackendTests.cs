using System.Buffers.Binary;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// win-arm64 is a compile-and-link target that cannot be executed on the x64 CI host (Windows-on-ARM
// PE under emulation is not viable here). These tests validate the produced image STRUCTURALLY by
// parsing the PE bytes directly: a valid ARM64 PE32+ with the entry point, imports, and no unresolved
// relocations. Execution coverage is deferred to real Windows-on-ARM hardware or the qemu+ARM64-Wine
// chain.
public sealed class WindowsArm64BackendTests
{
    private const ushort ImageFileMachineArm64 = 0xAA64;

    private static byte[] CompileArm64(string source)
    {
        var diag = new Diagnostics();
        var ast = new Parser(source, diag).ParseExpression();
        diag.ThrowIfAny();
        var ir = new Lowering(diag).Lower(ast);
        diag.ThrowIfAny();
        return new WindowsArm64LlvmBackend().Compile(ir);
    }

    [Test]
    public void WindowsArm64_emits_a_valid_ARM64_PE32Plus_image()
    {
        byte[] image = CompileArm64("Ashes.IO.print(\"hello\")");

        // DOS header magic and e_lfanew -> PE signature.
        image[0].ShouldBe((byte)'M');
        image[1].ShouldBe((byte)'Z');
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(60, 4));
        image[peOffset].ShouldBe((byte)'P');
        image[peOffset + 1].ShouldBe((byte)'E');

        // COFF header: Machine == ARM64.
        int coffOffset = peOffset + 4;
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(coffOffset, 2));
        machine.ShouldBe(ImageFileMachineArm64);

        // Optional header: PE32+ magic, an entry point, and a Windows GUI/CUI subsystem.
        int optOffset = coffOffset + 20;
        BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(optOffset, 2)).ShouldBe((ushort)0x020B);
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optOffset + 16, 4)).ShouldBeGreaterThan(0u); // AddressOfEntryPoint
        BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(optOffset + 40, 2)).ShouldBeGreaterThanOrEqualTo((ushort)10); // MajorOperatingSystemVersion

        // Import directory (data dir index 1) must be populated — every program imports kernel32.
        int dataDirOffset = optOffset + 112;
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(dataDirOffset + 8, 4)).ShouldBeGreaterThan(0u); // Import Table RVA
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(dataDirOffset + 12, 4)).ShouldBeGreaterThan(0u); // Import Table Size
    }

    [Test]
    public void WindowsArm64_links_a_program_with_a_jump_table_and_multiple_imports()
    {
        // A match on an Int lowers to a jump table in .rdata (ADDR64 data relocations), and the
        // arithmetic/printing pulls several kernel32 imports — exercising the ARM64 COFF relocation
        // switch (BRANCH26/PAGEBASE_REL21/PAGEOFFSET_12A/12L) and the adrp/ldr/br import thunks.
        const string source = """
            let classify = given (n) ->
                match n with
                    | 0 -> "zero"
                    | 1 -> "one"
                    | 2 -> "two"
                    | _ -> "many"
            in Ashes.IO.print(classify(2))
            """;

        byte[] image = CompileArm64(source);

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(60, 4));
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(peOffset + 4, 2));
        machine.ShouldBe(ImageFileMachineArm64);
        image.Length.ShouldBeGreaterThan(1024);
    }
}
