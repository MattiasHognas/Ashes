using System.Buffers.Binary;

namespace Ashes.Backend.Llvm;

// ARM64 (AArch64) PE/COFF image linker. Shares the arch-neutral header/section/import machinery in
// LlvmImageLinkerPe.cs (threaded via WindowsImageArch) and reuses the AArch64 instruction-immediate
// encoders in LlvmImageLinkerElfArm64.cs. The ARM64-specific pieces here are: the COFF relocation
// set, the adrp/ldr/br import call thunk, the entry trampoline, and the x15-convention __chkstk stub.
// The trampoline and thunk instruction words are assembled from the same encodings verified with
// llvm-mc; the pc-relative fields are patched with the shared ELF encoders.
internal static partial class LlvmImageLinker
{
    // COFF ARM64 relocation types (winnt.h IMAGE_REL_ARM64_*).
    private const ushort CoffRelocArm64Addr32 = 0x0001;
    private const ushort CoffRelocArm64Branch26 = 0x0003;
    private const ushort CoffRelocArm64PageBaseRel21 = 0x0004;
    private const ushort CoffRelocArm64Rel21 = 0x0005;
    private const ushort CoffRelocArm64PageOffset12A = 0x0006;
    private const ushort CoffRelocArm64PageOffset12L = 0x0007;
    private const ushort CoffRelocArm64Addr64 = 0x000E;

    // AArch64 instruction words (base encodings; immediates patched in).
    private const uint Arm64InsnBl = 0x94000000;         // bl   #imm26
    private const uint Arm64InsnMovzW0Zero = 0x52800000; // mov  w0, #0
    private const uint Arm64InsnAdrpX16 = 0x90000010;    // adrp x16, #page
    private const uint Arm64InsnLdrX16X16 = 0xF9400210;  // ldr  x16, [x16, #lo12]
    private const uint Arm64InsnBlrX16 = 0xD63F0200;     // blr  x16
    private const uint Arm64InsnBrX16 = 0xD61F0200;      // br   x16
    private const uint Arm64InsnBrk0 = 0xD4200000;       // brk  #0

    public static byte[] LinkWindowsArm64Executable(
        byte[] objectBytes,
        string entrySymbolName,
        LinkedImagePayload? linkedPayload = null,
        IReadOnlyDictionary<string, string>? externalLibraries = null)
    {
        string? dumpPath = Environment.GetEnvironmentVariable("ASH_DUMP_WINARM64_OBJ");
        if (!string.IsNullOrEmpty(dumpPath))
        {
            System.IO.File.WriteAllBytes(dumpPath, objectBytes);
        }

        return LinkWindowsImage(WindowsImageArch.Arm64, objectBytes, entrySymbolName, linkedPayload, externalLibraries);
    }

    // Entry trampoline (24 bytes): bl entry; mov w0,#0; adrp/ldr the ExitProcess IAT slot; blr; brk.
    private static byte[] BuildWindowsArm64Trampoline(WindowsImageArch arch, int entryOffsetInText, ulong exitProcessIatVa)
    {
        var bytes = new byte[arch.TrampolineLength];
        WriteInsn(bytes, 0, Arm64InsnBl);
        WriteInsn(bytes, 4, Arm64InsnMovzW0Zero);
        WriteInsn(bytes, 8, Arm64InsnAdrpX16);
        WriteInsn(bytes, 12, Arm64InsnLdrX16X16);
        WriteInsn(bytes, 16, Arm64InsnBlrX16);
        WriteInsn(bytes, 20, Arm64InsnBrk0);

        ulong trampolineVa = PeImageBase + PeTextRva;
        long entryVa = (long)(PeImageBase + PeTextRva + (uint)arch.TextPrefixLength) + entryOffsetInText;
        ApplyElfArm64Branch26Relocation(bytes, 0, entryVa, (long)trampolineVa);
        ApplyElfArm64AdrpPageRelocation(bytes, 8, (long)exitProcessIatVa, (long)(trampolineVa + 8));
        // ldr x16, [x16, #lo12] — 64-bit access, scale 3.
        ApplyElfArm64ScaledImm12Relocation(bytes, 12, (long)exitProcessIatVa, 3);
        return bytes;
    }

    // Windows/ARM64 __chkstk (32 bytes, self-contained — no relocations). x15 holds the frame size
    // in 16-byte units; probes each 4 KiB guard page in [sp - x15*16, sp), preserving x15 and every
    // register except x16/x17. The caller (LLVM) does `sub sp, sp, x15, lsl #4` after it returns.
    // Assembled + disassembly-verified with llvm-mc.
    private static byte[] BuildWindowsArm64ChkstkStub()
    {
        return
        [
            0xf0, 0xed, 0x7c, 0xd3, // lsl  x16, x15, #4
            0xf1, 0x03, 0x00, 0x91, // mov  x17, sp
            0x10, 0x06, 0x40, 0xf1, // subs x16, x16, #0x1000
            0x89, 0x00, 0x00, 0x54, // b.ls #+0x14  (to ret)
            0x31, 0x06, 0x40, 0xd1, // sub  x17, x17, #0x1000
            0x3f, 0x02, 0x40, 0xf9, // ldr  xzr, [x17]
            0xfc, 0xff, 0xff, 0x17, // b    #-0x10  (loop)
            0xc0, 0x03, 0x5f, 0xd6, // ret
        ];
    }

    // Import call thunk. x64: jmp qword ptr [rip+disp32] + int3 padding (8 bytes). arm64:
    // adrp x16,page(iat); ldr x16,[x16,#lo12]; br x16 (12 bytes).
    private static void WriteWindowsImportThunk(WindowsImageArch arch, byte[] buffer, int offset, ulong thunkVa, ulong iatEntryVa)
    {
        if (!arch.IsArm64)
        {
            buffer[offset] = 0xFF; // jmp qword ptr [rip+disp32]
            buffer[offset + 1] = 0x25;
            BinaryPrimitives.WriteInt32LittleEndian(
                buffer.AsSpan(offset + 2, 4),
                checked((int)((long)iatEntryVa - (long)(thunkVa + 6))));
            buffer[offset + 6] = 0xCC; // int3 padding
            buffer[offset + 7] = 0xCC;
            return;
        }

        WriteInsn(buffer, offset, Arm64InsnAdrpX16);
        WriteInsn(buffer, offset + 4, Arm64InsnLdrX16X16);
        WriteInsn(buffer, offset + 8, Arm64InsnBrX16);
        ApplyElfArm64AdrpPageRelocation(buffer, (ulong)offset, (long)iatEntryVa, (long)thunkVa);
        ApplyElfArm64ScaledImm12Relocation(buffer, (ulong)(offset + 4), (long)iatEntryVa, 3);
    }

    // Applies one COFF AArch64 .text/.data relocation. The instruction-immediate encoders are shared
    // with the ELF AArch64 linker (arch-defined encodings, format-independent). Our codegen emits a
    // zero in-place addend for the instruction relocations (verified), so the symbol VA is the exact
    // target; ADDR64/ADDR32 read the stored value as the addend, matching the x64 data path.
    private static void ApplyCoffArm64Relocation(
        ushort relocationType,
        byte[] targetBytes,
        int patchOffset,
        long targetVa,
        long placeVa)
    {
        switch (relocationType)
        {
            case CoffRelocArm64Branch26:
                ApplyElfArm64Branch26Relocation(targetBytes, (ulong)patchOffset, targetVa, placeVa);
                break;
            case CoffRelocArm64PageBaseRel21:
            case CoffRelocArm64Rel21:
                ApplyElfArm64AdrpPageRelocation(targetBytes, (ulong)patchOffset, targetVa, placeVa);
                break;
            case CoffRelocArm64PageOffset12A:
                ApplyElfArm64ScaledImm12Relocation(targetBytes, (ulong)patchOffset, targetVa, 0);
                break;
            case CoffRelocArm64PageOffset12L:
                ApplyElfArm64ScaledImm12Relocation(targetBytes, (ulong)patchOffset, targetVa, DetermineArm64LdstScale(targetBytes, patchOffset));
                break;
            case CoffRelocArm64Addr64:
                Span<byte> patch64 = targetBytes.AsSpan(patchOffset, 8);
                ulong addend64 = BinaryPrimitives.ReadUInt64LittleEndian(patch64);
                BinaryPrimitives.WriteUInt64LittleEndian(patch64, checked((ulong)targetVa + addend64));
                break;
            case CoffRelocArm64Addr32:
                Span<byte> patch32 = targetBytes.AsSpan(patchOffset, 4);
                int addend32 = BinaryPrimitives.ReadInt32LittleEndian(patch32);
                BinaryPrimitives.WriteUInt32LittleEndian(patch32, checked((uint)(targetVa + addend32)));
                break;
            default:
                throw new InvalidOperationException($"LLVM COFF emitted unsupported AArch64 relocation type 0x{relocationType:X4}.");
        }
    }

    // PAGEOFFSET_12L scale comes from the load/store size field (bits 31:30) of the patched
    // instruction: 1/2/4/8-byte -> shift 0/1/2/3. (128-bit SIMD would be 4, not emitted by codegen.)
    private static int DetermineArm64LdstScale(byte[] textBytes, int patchOffset)
    {
        uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(textBytes.AsSpan(patchOffset, 4));
        return (int)((instruction >> 30) & 0x3);
    }

    private static void WriteInsn(byte[] buffer, int offset, uint instruction) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), instruction);
}
