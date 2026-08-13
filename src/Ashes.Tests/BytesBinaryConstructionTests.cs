using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class BytesBinaryConstructionTests
{
    [Test]
    public void Nested_updates_reuse_fresh_temporaries_but_named_aliases_copy()
    {
        IrProgram nested = LowerProgram(
            "Ashes.Byte.setU32Le(Ashes.Byte.set(Ashes.Byte.allocate(8))(0)(1u8))(1)(2u32)");
        IrProgram aliased = LowerProgram(
            "let original = Ashes.Byte.allocate(8)\nlet patched = Ashes.Byte.set(original)(0)(1u8)\nAshes.Byte.get(original)(0) + Ashes.Byte.get(patched)(0)");

        nested.EntryFunction.Instructions.OfType<IrInst.BytesSet>().Single().ReuseInput.ShouldBeTrue();
        nested.EntryFunction.Instructions.OfType<IrInst.BytesSetU32Le>().Single().ReuseInput.ShouldBeTrue();
        aliased.EntryFunction.Instructions.OfType<IrInst.BytesSet>().Single().ReuseInput.ShouldBeFalse();
    }

    [Test]
    public async Task Model_generated_patch_sequence_matches_reference_bytes()
    {
        const int bufferLength = 257;
        byte[] expected = new byte[bufferLength];
        byte[] sourceBytes = Enumerable.Range(0, 64).Select(index => (byte)(index * 37)).ToArray();
        string sourceLiteral = "[" + string.Join(", ", sourceBytes.Select(value => value + "u8")) + "]";
        string expression = $"Ashes.Byte.allocate({bufferLength})";
        Random random = new(20260813);

        for (int operation = 0; operation < 96; operation++)
        {
            int kind = random.Next(4);
            int width = kind switch { 0 => 1, 1 => 2, 2 => 4, _ => 8 };
            int offset = random.Next(bufferLength - width + 1);
            ulong value = NextUInt64(random);
            expression = kind switch
            {
                0 => $"Ashes.Byte.set({expression})({offset})({(byte)value}u8)",
                1 => $"Ashes.Byte.setU16Le({expression})({offset})({(ushort)value}u16)",
                2 => $"Ashes.Byte.setU32Le({expression})({offset})({(uint)value}u32)",
                _ => $"Ashes.Byte.setU64Le({expression})({offset})({value}u64)",
            };
            WriteModelValue(expected, offset, value, width);

            if (operation % 11 == 0)
            {
                int length = random.Next(sourceBytes.Length + 1);
                int sourceOffset = random.Next(sourceBytes.Length - length + 1);
                int destinationOffset = random.Next(bufferLength - length + 1);
                expression = $"Ashes.Byte.copyRange({expression})({destinationOffset})(source)({sourceOffset})({length})";
                sourceBytes.AsSpan(sourceOffset, length).CopyTo(expected.AsSpan(destinationOffset, length));
            }
        }

        string source = $"let source = Ashes.Byte.fromList({sourceLiteral})\nAshes.IO.writeBytes({expression})";
        IrProgram program = LowerProgram(source);
        program.EntryFunction.Instructions.OfType<IrInst.BytesAllocate>().Count().ShouldBe(1);
        program.EntryFunction.Instructions.OfType<IrInst.BytesSet>().ShouldAllBe(value => value.ReuseInput);
        program.EntryFunction.Instructions.OfType<IrInst.BytesSetU16Le>().ShouldAllBe(value => value.ReuseInput);
        program.EntryFunction.Instructions.OfType<IrInst.BytesSetU32Le>().ShouldAllBe(value => value.ReuseInput);
        program.EntryFunction.Instructions.OfType<IrInst.BytesSetU64Le>().ShouldAllBe(value => value.ReuseInput);
        program.EntryFunction.Instructions.OfType<IrInst.BytesCopyRange>().ShouldAllBe(value => value.ReuseInput);
        program.EntryFunction.Instructions.Count(instruction => instruction is IrInst.BytesSet
            or IrInst.BytesSetU16Le
            or IrInst.BytesSetU32Le
            or IrInst.BytesSetU64Le
            or IrInst.BytesCopyRange).ShouldBe(105);

        byte[] actual = await CompileAndRunBytesAsync(program).ConfigureAwait(false);
        actual.ShouldBe(expected);
    }

    [Test]
    public void Binary_construction_lowers_for_every_target_abi()
    {
        const string source =
            "Ashes.IO.print(Ashes.Byte.length(Ashes.Byte.setU64Le(Ashes.Byte.allocate(16))(3)(72623859790382856u64)))";

        ReadElfMachine(new LinuxX64LlvmBackend().Compile(LowerProgram(source))).ShouldBe((ushort)62);
        ReadElfMachine(new LinuxArm64LlvmBackend().Compile(LowerProgram(source))).ShouldBe((ushort)183);
        ReadPeMachine(new WindowsX64LlvmBackend().Compile(LowerProgram(source))).ShouldBe((ushort)0x8664);
        ReadPeMachine(new WindowsArm64LlvmBackend().Compile(LowerProgram(source))).ShouldBe((ushort)0xAA64);
    }

    private static ulong NextUInt64(Random random)
    {
        Span<byte> bytes = stackalloc byte[8];
        random.NextBytes(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteModelValue(byte[] bytes, int offset, ulong value, int width)
    {
        for (int index = 0; index < width; index++)
        {
            bytes[offset + index] = (byte)(value >> (index * 8));
        }
    }

    private static IrProgram LowerProgram(string source)
    {
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program ast = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram program = new Lowering(diagnostics).Lower(ast);
        diagnostics.ThrowIfAny();
        return program;
    }

    private static async Task<byte[]> CompileAndRunBytesAsync(IrProgram program)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ashes-bytes-builder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "bytes-builder-test");
        try
        {
            TestProcessHelper.WriteExecutable(executable, new LinuxX64LlvmBackend().Compile(program));
            ProcessStartInfo startInfo = new(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process process = await TestProcessHelper.StartProcessAsync(startInfo).ConfigureAwait(false);
            using MemoryStream output = new();
            Task copyOutput = process.StandardOutput.BaseStream.CopyToAsync(output);
            string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await Task.WhenAll(copyOutput, process.WaitForExitAsync()).ConfigureAwait(false);
            process.ExitCode.ShouldBe(0, stderr);
            return output.ToArray();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ushort ReadElfMachine(byte[] image)
    {
        image.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }).ShouldBeTrue();
        return BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(18, 2));
    }

    private static ushort ReadPeMachine(byte[] image)
    {
        Encoding.ASCII.GetString(image, 0, 2).ShouldBe("MZ");
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C, 4));
        return BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(peOffset + 4, 2));
    }
}
