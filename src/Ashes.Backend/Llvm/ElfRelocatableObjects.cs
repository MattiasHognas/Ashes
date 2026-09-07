using System.Buffers.Binary;

namespace Ashes.Backend.Llvm;

/// <summary>
/// Merges several ELF64 relocatable objects into one, the way a relocatable link does: sections
/// with the same name are concatenated with their alignment, every symbol is rebased into the
/// merged section it lands in, relocations keep their type and move with their section, and an
/// undefined symbol one object references resolves to the definition another object provides.
/// The relocation types are opaque to the merge, so the same code serves every ELF target.
/// </summary>
internal static partial class ElfRelocatableObjects
{
    private const uint SectionTypeProgBits = 1;
    private const uint SectionTypeSymtab = 2;
    private const uint SectionTypeStrtab = 3;
    private const uint SectionTypeRela = 4;
    private const uint SectionTypeNote = 7;
    private const uint SectionTypeNoBits = 8;
    private const uint SectionTypeRel = 9;
    private const uint SectionTypeLlvmAddrsig = 0x6fff4c03;
    private const uint SectionTypeLowProcessor = 0x70000000;
    private const uint SectionTypeHighProcessor = 0x7fffffff;
    private const ulong SectionFlagMerge = 0x10;
    private const ulong SectionFlagInfoLink = 0x40;
    private const ushort SectionIndexLowReserved = 0xff00;
    private const ushort SectionIndexCommon = 0xfff2;
    private const byte SymbolBindingLocal = 0;
    private const byte SymbolBindingGlobal = 1;
    private const byte SymbolBindingWeak = 2;
    private const byte SymbolTypeSection = 3;
    private const int ElfHeaderSize = 64;
    private const int SectionHeaderSize = 64;
    private const int SymbolSize = 24;
    private const int RelaSize = 24;

    /// <summary>Whether <see cref="Merge"/> is implemented.</summary>
    public static bool MergeSupported => true;

    public static byte[] Merge(IReadOnlyList<byte[]> objects)
    {
        if (objects.Count == 1)
        {
            return objects[0];
        }

        var inputs = new InputObject[objects.Count];
        var state = new MergeState();
        for (int index = 0; index < objects.Count; index++)
        {
            inputs[index] = InputObject.Parse(objects[index]);
            if (inputs[index].Machine != inputs[0].Machine)
            {
                throw new InvalidOperationException(
                    $"ELF objects for machines {inputs[0].Machine} and {inputs[index].Machine} cannot be merged.");
            }

            state.PlaceSections(inputs[index]);
        }

        state.AddSectionSymbols();
        foreach (InputObject input in inputs)
        {
            state.CollectLocalSymbols(input);
        }

        foreach (InputObject input in inputs)
        {
            state.CollectGlobalSymbols(input);
        }

        state.AssignGlobalSymbolIndices();
        foreach (InputObject input in inputs)
        {
            state.CollectRelocations(input);
        }

        return state.Write(inputs[0]);
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        return (value + alignment - 1) / alignment * alignment;
    }

    private readonly record struct SectionHeader(
        uint NameOffset,
        uint Type,
        ulong Flags,
        ulong Offset,
        ulong Size,
        uint Link,
        uint Info,
        ulong AddressAlign,
        ulong EntrySize);

    private readonly record struct Symbol(
        uint NameOffset,
        byte Info,
        byte Other,
        ushort SectionIndex,
        ulong Value,
        ulong Size)
    {
        public byte Binding => (byte)(Info >> 4);

        public byte Type => (byte)(Info & 0xF);
    }

    // A symbol of the merged object: its name, the section index it lands in, and its value
    // rebased into that section.
    private sealed record OutputSymbol(
        string Name,
        byte Info,
        byte Other,
        ushort SectionIndex,
        ulong Value,
        ulong Size);

    // One input object with the maps the merge builds for it: which output section each of its
    // sections went to and at what offset, and which output symbol each of its symbols became.
    private sealed class InputObject
    {
        private InputObject(byte[] bytes, ushort machine, uint flags, SectionHeader[] sections, string[] sectionNames, int symtabIndex, byte[] strtab)
        {
            Bytes = bytes;
            Machine = machine;
            Flags = flags;
            Sections = sections;
            SectionNames = sectionNames;
            SymtabIndex = symtabIndex;
            Strtab = strtab;
            SymbolCount = checked((int)(sections[symtabIndex].Size / SymbolSize));
            SectionMap = new int[sections.Length];
            SectionOffsets = new ulong[sections.Length];
            SymbolMap = new int[SymbolCount];
            SectionSymbolAddends = new ulong[SymbolCount];
            Array.Fill(SectionMap, -1);
            Array.Fill(SymbolMap, -1);
            SymbolMap[0] = 0;
        }

        public byte[] Bytes { get; }

        public ushort Machine { get; }

        public uint Flags { get; }

        public SectionHeader[] Sections { get; }

        public string[] SectionNames { get; }

        public int SymtabIndex { get; }

        public byte[] Strtab { get; }

        public int SymbolCount { get; }

        public int[] SectionMap { get; }

        public ulong[] SectionOffsets { get; }

        public int[] SymbolMap { get; }

        public ulong[] SectionSymbolAddends { get; }

        public static InputObject Parse(byte[] bytes)
        {
            ReadOnlySpan<byte> span = bytes;
            if (span.Length < ElfHeaderSize || span[0] != 0x7F || span[1] != (byte)'E' || span[2] != (byte)'L' || span[3] != (byte)'F')
            {
                throw new InvalidOperationException("LLVM did not emit a valid ELF object.");
            }

            if (span[4] != 2 || span[5] != 1)
            {
                throw new InvalidOperationException("Only ELF64 little-endian LLVM objects can be merged.");
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(16, 2)) != 1)
            {
                throw new InvalidOperationException("Only relocatable ELF objects can be merged.");
            }

            ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(18, 2));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(48, 4));
            SectionHeader[] sections = ReadSectionHeaders(span);
            ushort sectionNamesIndex = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(62, 2));
            byte[] sectionNameTable = ReadSectionBytes(span, sections[sectionNamesIndex]);
            var sectionNames = new string[sections.Length];
            int symtabIndex = -1;
            for (int index = 0; index < sections.Length; index++)
            {
                sectionNames[index] = ReadString(sectionNameTable, sections[index].NameOffset);
                if (sections[index].Type == SectionTypeSymtab)
                {
                    symtabIndex = index;
                }
            }

            if (symtabIndex < 0)
            {
                throw new InvalidOperationException("LLVM object did not contain a symbol table.");
            }

            if (sections[symtabIndex].EntrySize != SymbolSize)
            {
                throw new InvalidOperationException("LLVM symbol table has an unexpected entry size.");
            }

            byte[] strtab = ReadSectionBytes(span, sections[checked((int)sections[symtabIndex].Link)]);
            return new InputObject(bytes, machine, flags, sections, sectionNames, symtabIndex, strtab);
        }

        public Symbol ReadSymbol(int index)
        {
            ReadOnlySpan<byte> span = Bytes;
            int offset = checked((int)Sections[SymtabIndex].Offset + index * SymbolSize);
            return new Symbol(
                NameOffset: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4)),
                Info: span[offset + 4],
                Other: span[offset + 5],
                SectionIndex: BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 6, 2)),
                Value: BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 8, 8)),
                Size: BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 16, 8)));
        }

        public string SymbolName(Symbol symbol) => ReadString(Strtab, symbol.NameOffset);

        // A section's file image; a NOBITS section occupies no file bytes.
        public ReadOnlySpan<byte> SectionBytes(SectionHeader section) =>
            section.Type == SectionTypeNoBits
                ? ReadOnlySpan<byte>.Empty
                : Bytes.AsSpan(checked((int)section.Offset), checked((int)section.Size));

        private static SectionHeader[] ReadSectionHeaders(ReadOnlySpan<byte> span)
        {
            ulong sectionHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(40, 8));
            ushort sectionHeaderEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(58, 2));
            ushort sectionHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(60, 2));
            if (sectionHeaderEntrySize < SectionHeaderSize || sectionHeaderCount == 0)
            {
                throw new InvalidOperationException("LLVM object is missing ELF section headers.");
            }

            var sections = new SectionHeader[sectionHeaderCount];
            for (int index = 0; index < sectionHeaderCount; index++)
            {
                int offset = checked((int)sectionHeaderOffset + index * sectionHeaderEntrySize);
                sections[index] = new SectionHeader(
                    NameOffset: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4)),
                    Type: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4)),
                    Flags: BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 8, 8)),
                    Offset: BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 24, 8)),
                    Size: BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 32, 8)),
                    Link: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 40, 4)),
                    Info: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 44, 4)),
                    AddressAlign: BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 48, 8)),
                    EntrySize: BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 56, 8)));
            }

            return sections;
        }

        private static byte[] ReadSectionBytes(ReadOnlySpan<byte> span, SectionHeader section) =>
            section.Type == SectionTypeNoBits
                ? []
                : span.Slice(checked((int)section.Offset), checked((int)section.Size)).ToArray();

        private static string ReadString(byte[] table, uint offset)
        {
            int start = checked((int)offset);
            int end = Array.IndexOf(table, (byte)0, start);
            if (end < 0)
            {
                end = table.Length;
            }

            return System.Text.Encoding.UTF8.GetString(table, start, end - start);
        }
    }
}
