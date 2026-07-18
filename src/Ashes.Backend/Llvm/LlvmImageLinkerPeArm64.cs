namespace Ashes.Backend.Llvm;

// ARM64 (AArch64) PE/COFF image linker. Shares the arch-neutral header/section/import machinery
// in LlvmImageLinkerPe.cs and reuses the AArch64 instruction-immediate encoders in
// LlvmImageLinkerElfArm64.cs; the ARM64-specific pieces are the COFF relocation set, the
// adrp/ldr/br import thunk, the entry trampoline, and the x15-convention __chkstk stub.
internal static partial class LlvmImageLinker
{
    public static byte[] LinkWindowsArm64Executable(
        byte[] objectBytes,
        string entrySymbolName,
        LinkedImagePayload? linkedPayload = null,
        IReadOnlyDictionary<string, string>? externalLibraries = null)
    {
        // Phase 4 implements the ARM64 PE image writer. Until then, emitting a win-arm64 program
        // reaches valid AArch64/Windows object code (verifiable via llvm tools on the object) but
        // cannot yet be linked into a final PE image. Dev aid: dump the COFF object for inspection.
        string? dumpPath = Environment.GetEnvironmentVariable("ASH_DUMP_WINARM64_OBJ");
        if (!string.IsNullOrEmpty(dumpPath))
        {
            System.IO.File.WriteAllBytes(dumpPath, objectBytes);
        }

        throw new NotSupportedException(
            "win-arm64 image linking is not yet implemented (Phase 4).");
    }
}
