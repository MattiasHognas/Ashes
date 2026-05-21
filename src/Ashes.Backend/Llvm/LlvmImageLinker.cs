namespace Ashes.Backend.Llvm;

internal static partial class LlvmImageLinker
{
    internal readonly record struct LinkedImagePayload(string StartSymbolName, string EndSymbolName, byte[] Bytes, int Alignment);

    private static int Align(int value, int align)
    {
        int mask = align - 1;
        return (value + mask) & ~mask;
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        uint remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }
}
