using System.Buffers.Binary;
using System.Text;

namespace Ashes.Backend.Llvm;

internal static partial class CoffRelocatableObjects
{
    private readonly record struct InputRelocation(uint VirtualAddress, int SymbolIndex, ushort Type);

    /// <summary>One input object: its bytes, sections, symbols, and the map from each of its
    /// symbol indices to the merged symbol table (-1 for a symbol the merge discards).</summary>
    private sealed class InputObject(byte[] bytes, int index)
    {
        public byte[] Bytes { get; } = bytes;

        public int Index { get; } = index;

        public ushort Machine { get; init; }

        public int StringTableOffset { get; init; }

        public InputSection[] Sections { get; set; } = [];

        /// <summary>The symbols by symbol index; an auxiliary record's slot is null.</summary>
        public InputSymbol?[] Symbols { get; set; } = [];

        public int[] SymbolMap { get; set; } = [];

        public InputRelocation ReadRelocation(InputSection section, int index)
        {
            int offset = checked(section.RelocationRecordsOffset + (index * RelocationRecordSize));
            ReadOnlySpan<byte> record = Bytes.AsSpan(offset, RelocationRecordSize);
            return new InputRelocation(
                BinaryPrimitives.ReadUInt32LittleEndian(record[..4]),
                checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(4, 4))),
                BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(8, 2)));
        }

        public ReadOnlySpan<byte> AuxiliaryRecords(InputSymbol symbol) =>
            Bytes.AsSpan(symbol.RecordOffset + SymbolRecordSize, symbol.AuxCount * SymbolRecordSize);
    }

    /// <summary>An input section and where it lands in the merged object: its merged section
    /// and offset there, or the survivor it was dropped for when it is a duplicate COMDAT.</summary>
    private sealed class InputSection(InputObject owner, int number, string name, uint characteristics)
    {
        public InputObject Owner { get; } = owner;

        /// <summary>The 1-based section number in the input object.</summary>
        public int Number { get; } = number;

        public string Name { get; } = name;

        public uint Characteristics { get; } = characteristics;

        public uint Size { get; init; }

        public uint PointerToRawData { get; init; }

        public int RelocationRecordsOffset { get; init; }

        public int RelocationCount { get; init; }

        public bool IsComdat => (Characteristics & SectionLinkComdat) != 0;

        public bool IsUninitialized => (Characteristics & SectionUninitializedData) != 0;

        public uint Alignment => SectionAlignment(Characteristics);

        public byte ComdatSelection { get; set; }

        public int AssociatedSectionNumber { get; set; }

        public string? ComdatName { get; set; }

        public int SectionSymbolIndex { get; set; } = -1;

        public bool Dropped { get; private set; }

        public InputSection? Replacement { get; private set; }

        public OutputSection? Output { get; set; }

        public uint Offset { get; set; }

        public int MarkerSymbolIndex { get; set; } = -1;

        public ReadOnlySpan<byte> RawData =>
            IsUninitialized || Size == 0 || PointerToRawData == 0 ? default : Owner.Bytes.AsSpan(checked((int)PointerToRawData), checked((int)Size));

        public void DropFor(InputSection? replacement)
        {
            Dropped = true;
            Replacement = replacement;
        }

        /// <summary>The section this one's contents live in after the merge: itself, the
        /// survivor of its COMDAT, or null when it was discarded outright.</summary>
        public InputSection? Resolve()
        {
            InputSection? section = this;
            while (section is not null && section.Dropped)
            {
                section = section.Replacement;
            }

            return section;
        }
    }

    private sealed class InputSymbol(int index, int recordOffset, string name)
    {
        public int Index { get; } = index;

        public int RecordOffset { get; } = recordOffset;

        public string Name { get; } = name;

        public uint Value { get; init; }

        public short SectionNumber { get; init; }

        public ushort Type { get; init; }

        public byte StorageClass { get; init; }

        public byte AuxCount { get; init; }
    }

    private static InputObject ParseInput(byte[] bytes, int index)
    {
        if (bytes.Length < FileHeaderSize)
        {
            throw new InvalidOperationException($"COFF object {index} is too short to carry a file header.");
        }

        ReadOnlySpan<byte> header = bytes.AsSpan(0, FileHeaderSize);
        ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(2, 2));
        int symbolTableOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4)));
        int symbolCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4)));
        var input = new InputObject(bytes, index)
        {
            Machine = BinaryPrimitives.ReadUInt16LittleEndian(header[..2]),
            StringTableOffset = checked(symbolTableOffset + (symbolCount * SymbolRecordSize)),
        };

        input.Sections = ParseSections(input, sectionCount);
        input.Symbols = ParseSymbols(input, symbolTableOffset, symbolCount);
        input.SymbolMap = new int[symbolCount];
        Array.Fill(input.SymbolMap, -1);
        NameComdatSections(input);
        return input;
    }

    // A COMDAT section is named by its leader: the first symbol defined in the section after
    // the section symbol itself.
    private static void NameComdatSections(InputObject input)
    {
        foreach (InputSection section in input.Sections)
        {
            if (!section.IsComdat || section.ComdatSelection == ComdatSelectAssociative || section.SectionSymbolIndex < 0)
            {
                continue;
            }

            for (int i = section.SectionSymbolIndex + 1; i < input.Symbols.Length; i++)
            {
                InputSymbol? symbol = input.Symbols[i];
                if (symbol is not null && symbol.SectionNumber == section.Number)
                {
                    section.ComdatName = symbol.Name;
                    break;
                }
            }
        }
    }

    private static InputSection[] ParseSections(InputObject input, int sectionCount)
    {
        var sections = new InputSection[sectionCount];
        for (int i = 0; i < sectionCount; i++)
        {
            ReadOnlySpan<byte> header = input.Bytes.AsSpan(FileHeaderSize + (i * SectionHeaderSize), SectionHeaderSize);
            uint characteristics = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(36, 4));
            int relocationsOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24, 4)));
            int relocationCount = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(32, 2));
            if ((characteristics & SectionRelocationOverflow) != 0 && relocationCount == MaximumRelocationsPerSection)
            {
                // The overflow form keeps the true count in the first relocation record's
                // VirtualAddress, counting that record itself.
                relocationCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(input.Bytes.AsSpan(relocationsOffset, 4))) - 1;
                relocationsOffset += RelocationRecordSize;
            }

            sections[i] = new InputSection(input, i + 1, ReadSectionName(input, header[..8]), characteristics)
            {
                Size = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4)),
                PointerToRawData = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4)),
                RelocationRecordsOffset = relocationsOffset,
                RelocationCount = relocationCount,
            };
        }

        return sections;
    }

    private static InputSymbol?[] ParseSymbols(InputObject input, int symbolTableOffset, int symbolCount)
    {
        var symbols = new InputSymbol?[symbolCount];
        for (int i = 0; i < symbolCount; i++)
        {
            int offset = symbolTableOffset + (i * SymbolRecordSize);
            ReadOnlySpan<byte> record = input.Bytes.AsSpan(offset, SymbolRecordSize);
            var symbol = new InputSymbol(i, offset, ReadSymbolName(input, record[..8]))
            {
                Value = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(8, 4)),
                SectionNumber = BinaryPrimitives.ReadInt16LittleEndian(record.Slice(12, 2)),
                Type = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(14, 2)),
                StorageClass = record[16],
                AuxCount = record[17],
            };
            symbols[i] = symbol;
            if (i + symbol.AuxCount >= symbolCount)
            {
                throw new InvalidOperationException($"COFF object {input.Index} symbol {i} claims more auxiliary records than the table holds.");
            }

            AttachSectionDefinition(input, symbol);
            i += symbol.AuxCount;
        }

        return symbols;
    }

    // A section symbol is the static, zero-valued symbol carrying the section's name and its
    // section-definition auxiliary record, whose selection and associated section number
    // describe the COMDAT the section belongs to.
    private static void AttachSectionDefinition(InputObject input, InputSymbol symbol)
    {
        if (symbol.StorageClass != StorageClassStatic || symbol.AuxCount == 0 || symbol.Value != 0
            || symbol.SectionNumber < 1 || symbol.SectionNumber > input.Sections.Length)
        {
            return;
        }

        InputSection section = input.Sections[symbol.SectionNumber - 1];
        if (section.SectionSymbolIndex >= 0 || !string.Equals(section.Name, symbol.Name, StringComparison.Ordinal))
        {
            return;
        }

        ReadOnlySpan<byte> aux = input.AuxiliaryRecords(symbol);
        section.SectionSymbolIndex = symbol.Index;
        section.ComdatSelection = aux[14];
        if (section.ComdatSelection == ComdatSelectAssociative)
        {
            section.AssociatedSectionNumber = BinaryPrimitives.ReadUInt16LittleEndian(aux.Slice(12, 2));
        }
    }

    private static string ReadSymbolName(InputObject input, ReadOnlySpan<byte> nameBytes)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(nameBytes[..4]) == 0)
        {
            int nameOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(nameBytes.Slice(4, 4)));
            return ReadStringTableName(input, nameOffset);
        }

        return ReadInlineName(nameBytes);
    }

    // A section name longer than eight bytes is spelled "/<decimal offset into the string table>".
    private static string ReadSectionName(InputObject input, ReadOnlySpan<byte> nameBytes)
    {
        string inline = ReadInlineName(nameBytes);
        if (inline.Length > 1 && inline[0] == '/'
            && int.TryParse(inline.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int tableOffset))
        {
            return ReadStringTableName(input, tableOffset);
        }

        return inline;
    }

    private static string ReadInlineName(ReadOnlySpan<byte> nameBytes)
    {
        int length = nameBytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(length < 0 ? nameBytes : nameBytes[..length]);
    }

    private static string ReadStringTableName(InputObject input, int tableOffset)
    {
        int start = checked(input.StringTableOffset + tableOffset);
        ReadOnlySpan<byte> rest = input.Bytes.AsSpan(start);
        int length = rest.IndexOf((byte)0);
        return Encoding.ASCII.GetString(length < 0 ? rest : rest[..length]);
    }
}
