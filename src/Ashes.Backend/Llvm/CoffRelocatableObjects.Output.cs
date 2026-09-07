using System.Buffers.Binary;
using System.Text;

namespace Ashes.Backend.Llvm;

internal static partial class CoffRelocatableObjects
{
    private readonly record struct OutputRelocation(uint VirtualAddress, int SymbolIndex, ushort Type);

    /// <summary>A merged section: the input sections it concatenates, in order, and the
    /// relocations that moved with them.</summary>
    private sealed class OutputSection(int number, string name, uint flags)
    {
        /// <summary>The 1-based section number in the merged object.</summary>
        public int Number { get; } = number;

        public string Name { get; } = name;

        public uint Flags { get; } = flags;

        public uint Alignment { get; private set; } = 1;

        public uint Size { get; private set; }

        public List<InputSection> Parts { get; } = [];

        public List<OutputRelocation> Relocations { get; } = [];

        public bool IsUninitialized => (Flags & SectionUninitializedData) != 0;

        public bool RelocationsOverflow => Relocations.Count >= MaximumRelocationsPerSection;

        public uint PointerToRawData { get; set; }

        public uint PointerToRelocations { get; set; }

        public void Append(InputSection section)
        {
            uint offset = AlignUp(Size, section.Alignment);
            section.Output = this;
            section.Offset = offset;
            Size = checked(offset + section.Size);
            Alignment = Math.Max(Alignment, section.Alignment);
            Parts.Add(section);
        }
    }

    private sealed class OutputSymbol(string name, uint value, short sectionNumber, ushort type, byte storageClass, byte[] aux)
    {
        public string Name { get; } = name;

        public uint Value { get; set; } = value;

        public short SectionNumber { get; set; } = sectionNumber;

        public ushort Type { get; set; } = type;

        public byte StorageClass { get; set; } = storageClass;

        public byte[] Aux { get; set; } = aux;

        public int RecordCount => 1 + (Aux.Length / SymbolRecordSize);

        public bool IsDefined => SectionNumber != 0;
    }

    /// <summary>
    /// The merged symbol table. The merged sections' own symbols come first; then each input's
    /// symbols follow in its order, rebased into the merged sections. External symbols are shared
    /// by name across the inputs: a definition replaces an undefined reference in place (so
    /// relocations already mapped to it stay valid), an undefined symbol stays one entry, a symbol
    /// defined at one place by several inputs (a COMDAT survivor) is one entry, and of two distinct
    /// definitions the first stays external while the later one becomes static. Static symbols
    /// stay per input.
    /// </summary>
    private sealed class OutputSymbolTable
    {
        private readonly List<OutputSymbol> symbols = [];
        private readonly List<int> recordIndices = [];
        private readonly Dictionary<string, int> externals = new(StringComparer.Ordinal);
        private readonly List<(int Symbol, InputObject Input, int Tag)> weakTags = [];
        private int recordCount;

        public OutputSymbolTable(List<OutputSection> sections)
        {
            foreach (OutputSection section in sections)
            {
                Add(new OutputSymbol(section.Name, 0, checked((short)section.Number), 0, StorageClassStatic, new byte[SymbolRecordSize]));
                SectionSymbolIndices.Add(section.Number, symbols.Count - 1);
            }
        }

        public IReadOnlyList<OutputSymbol> Symbols => symbols;

        public int RecordCount => recordCount;

        public Dictionary<int, int> SectionSymbolIndices { get; } = [];

        public int RecordIndexOf(int symbolIndex) => recordIndices[symbolIndex];

        public void MapSymbols(InputObject input)
        {
            foreach (InputSymbol? symbol in input.Symbols)
            {
                if (symbol is null)
                {
                    continue;
                }

                input.SymbolMap[symbol.Index] = MapSymbol(input, symbol);
            }
        }

        // Resolves every weak external no object defined strongly the way a final link does: it
        // becomes an external symbol at its alternate's location, or keeps its auxiliary record
        // with the alternate's index remapped when the alternate is undefined too. The alternate
        // can follow the weak symbol in its object, so this runs after every input is mapped.
        public void ResolveWeakExternals()
        {
            foreach ((int symbolIndex, InputObject input, int tag) in weakTags.ToArray())
            {
                OutputSymbol weak = symbols[symbolIndex];
                if (tag < 0 || tag >= input.SymbolMap.Length || input.SymbolMap[tag] < 0)
                {
                    throw new InvalidOperationException($"COFF object {input.Index} weak external '{weak.Name}' names an alternate the merge discarded.");
                }

                OutputSymbol alternate = symbols[input.SymbolMap[tag]];
                if (alternate.IsDefined)
                {
                    Replace(symbolIndex, new OutputSymbol(weak.Name, alternate.Value, alternate.SectionNumber, weak.Type, StorageClassExternal, []));
                }
            }

            foreach ((int symbolIndex, InputObject input, int tag) in weakTags)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(symbols[symbolIndex].Aux.AsSpan(0, 4), checked((uint)RecordIndexOf(input.SymbolMap[tag])));
            }
        }

        private int Add(OutputSymbol symbol)
        {
            symbols.Add(symbol);
            recordIndices.Add(recordCount);
            recordCount += symbol.RecordCount;
            return symbols.Count - 1;
        }

        private int MapSymbol(InputObject input, InputSymbol symbol)
        {
            if (symbol.SectionNumber > 0)
            {
                if (symbol.SectionNumber > input.Sections.Length)
                {
                    throw new InvalidOperationException($"COFF object {input.Index} symbol '{symbol.Name}' names section {symbol.SectionNumber}, which does not exist.");
                }

                InputSection section = input.Sections[symbol.SectionNumber - 1];
                if (section.SectionSymbolIndex == symbol.Index)
                {
                    return MapSectionSymbol(section);
                }

                InputSection? placed = section.Resolve();
                if (placed?.Output is null)
                {
                    return -1;
                }

                return MapPlacedSymbol(input, symbol, checked((short)placed.Output.Number), checked(symbol.Value + placed.Offset));
            }

            return MapPlacedSymbol(input, symbol, symbol.SectionNumber, symbol.Value);
        }

        // An input section symbol stands for the section's start: at offset 0 of the merged
        // section that is the merged section's symbol, otherwise a static marker symbol at the
        // section's offset.
        private int MapSectionSymbol(InputSection section)
        {
            InputSection? placed = section.Resolve();
            if (placed?.Output is null)
            {
                return -1;
            }

            if (placed.Offset == 0)
            {
                return SectionSymbolIndices[placed.Output.Number];
            }

            if (placed.MarkerSymbolIndex < 0)
            {
                placed.MarkerSymbolIndex = Add(new OutputSymbol(placed.Name, placed.Offset, checked((short)placed.Output.Number), 0, StorageClassStatic, []));
            }

            return placed.MarkerSymbolIndex;
        }

        private int MapPlacedSymbol(InputObject input, InputSymbol symbol, short sectionNumber, uint value)
        {
            byte[] aux = input.AuxiliaryRecords(symbol).ToArray();
            var candidate = new OutputSymbol(symbol.Name, value, sectionNumber, symbol.Type, symbol.StorageClass, aux);
            if (symbol.StorageClass != StorageClassExternal && symbol.StorageClass != StorageClassWeakExternal)
            {
                return Add(candidate);
            }

            int index = MergeExternal(input, candidate);
            if (symbol.StorageClass == StorageClassWeakExternal && ReferenceEquals(symbols[index], candidate))
            {
                weakTags.Add((index, input, checked((int)BinaryPrimitives.ReadUInt32LittleEndian(aux.AsSpan(0, 4)))));
            }

            return index;
        }

        private int MergeExternal(InputObject input, OutputSymbol candidate)
        {
            if (!externals.TryGetValue(candidate.Name, out int index))
            {
                index = Add(candidate);
                externals[candidate.Name] = index;
                return index;
            }

            OutputSymbol existing = symbols[index];
            if (!candidate.IsDefined)
            {
                if (!existing.IsDefined && existing.StorageClass == StorageClassExternal && candidate.StorageClass == StorageClassWeakExternal)
                {
                    Replace(index, candidate);
                }

                return index;
            }

            if (!existing.IsDefined)
            {
                Replace(index, candidate);
                return index;
            }

            if (existing.SectionNumber == candidate.SectionNumber && existing.Value == candidate.Value)
            {
                return index;
            }

            if (candidate.StorageClass != StorageClassExternal)
            {
                throw new InvalidOperationException($"COFF object {input.Index} defines '{candidate.Name}', which an earlier object already defines.");
            }

            // A name several objects define outside a COMDAT: the first object's copy stays the
            // external definition and a later copy becomes static, so that object's own
            // references still reach it.
            candidate.StorageClass = StorageClassStatic;
            return Add(candidate);
        }

        // Replaces a symbol in place, keeping its index so the relocations already mapped to it
        // stay valid; a change in its auxiliary record count shifts the record indices behind it.
        private void Replace(int index, OutputSymbol replacement)
        {
            int delta = replacement.RecordCount - symbols[index].RecordCount;
            if (delta != 0)
            {
                recordCount += delta;
                for (int i = index + 1; i < recordIndices.Count; i++)
                {
                    recordIndices[i] += delta;
                }
            }

            weakTags.RemoveAll(entry => entry.Symbol == index);
            symbols[index] = replacement;
        }
    }

    private static byte[] WriteObject(ushort machine, List<OutputSection> sections, OutputSymbolTable symbols)
    {
        var stringTable = new MemoryStream();
        stringTable.Write(new byte[4]);
        uint cursor = checked((uint)(FileHeaderSize + (sections.Count * SectionHeaderSize)));
        foreach (OutputSection section in sections)
        {
            if (!section.IsUninitialized && section.Size > 0)
            {
                cursor = AlignUp(cursor, Math.Max(4, section.Alignment));
                section.PointerToRawData = cursor;
                cursor = checked(cursor + section.Size);
            }
        }

        foreach (OutputSection section in sections)
        {
            if (section.Relocations.Count > 0)
            {
                section.PointerToRelocations = cursor;
                cursor = checked(cursor + (uint)((section.Relocations.Count + (section.RelocationsOverflow ? 1 : 0)) * RelocationRecordSize));
            }
        }

        uint symbolTableOffset = cursor;
        var output = new byte[checked((int)(symbolTableOffset + ((uint)symbols.RecordCount * SymbolRecordSize)))];
        WriteFileHeader(output, machine, sections.Count, symbolTableOffset, symbols.RecordCount);
        for (int i = 0; i < sections.Count; i++)
        {
            WriteSectionHeader(output.AsSpan(FileHeaderSize + (i * SectionHeaderSize), SectionHeaderSize), sections[i], stringTable);
            WriteSectionContents(output, sections[i], symbols);
        }

        WriteSymbolTable(output.AsSpan(checked((int)symbolTableOffset)), symbols, sections, stringTable);
        byte[] stringTableBytes = stringTable.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(stringTableBytes.AsSpan(0, 4), checked((uint)stringTableBytes.Length));
        return [.. output, .. stringTableBytes];
    }

    private static void WriteFileHeader(byte[] output, ushort machine, int sectionCount, uint symbolTableOffset, int symbolRecordCount)
    {
        Span<byte> header = output.AsSpan(0, FileHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[..2], machine);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(2, 2), checked((ushort)sectionCount));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), symbolTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12, 4), checked((uint)symbolRecordCount));
    }

    private static void WriteSectionHeader(Span<byte> header, OutputSection section, MemoryStream stringTable)
    {
        WriteName(header[..8], section.Name, stringTable, sectionHeader: true);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), section.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(20, 4), section.PointerToRawData);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), section.PointerToRelocations);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.Slice(32, 2),
            section.RelocationsOverflow ? (ushort)MaximumRelocationsPerSection : checked((ushort)section.Relocations.Count));
        uint characteristics = section.Flags | EncodeSectionAlignment(section.Alignment);
        if (section.RelocationsOverflow)
        {
            characteristics |= SectionRelocationOverflow;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(36, 4), characteristics);
    }

    // Copies each part's bytes to its offset (the alignment padding between parts stays zero)
    // and writes the section's relocations; past 0xFFFF of them the overflow form leads with a
    // record whose VirtualAddress is the count including that record.
    private static void WriteSectionContents(byte[] output, OutputSection section, OutputSymbolTable symbols)
    {
        if (section.PointerToRawData != 0)
        {
            foreach (InputSection part in section.Parts)
            {
                part.RawData.CopyTo(output.AsSpan(checked((int)(section.PointerToRawData + part.Offset))));
            }
        }

        if (section.Relocations.Count == 0)
        {
            return;
        }

        int offset = checked((int)section.PointerToRelocations);
        if (section.RelocationsOverflow)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset, 4), checked((uint)(section.Relocations.Count + 1)));
            offset += RelocationRecordSize;
        }

        foreach (OutputRelocation relocation in section.Relocations)
        {
            Span<byte> record = output.AsSpan(offset, RelocationRecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record[..4], relocation.VirtualAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(4, 4), checked((uint)symbols.RecordIndexOf(relocation.SymbolIndex)));
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(8, 2), relocation.Type);
            offset += RelocationRecordSize;
        }
    }

    private static void WriteSymbolTable(Span<byte> table, OutputSymbolTable symbols, List<OutputSection> sections, MemoryStream stringTable)
    {
        foreach (OutputSection section in sections)
        {
            OutputSymbol sectionSymbol = symbols.Symbols[symbols.SectionSymbolIndices[section.Number]];
            Span<byte> aux = sectionSymbol.Aux;
            BinaryPrimitives.WriteUInt32LittleEndian(aux[..4], section.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(aux.Slice(4, 2), (ushort)Math.Min(section.Relocations.Count, MaximumRelocationsPerSection));
            BinaryPrimitives.WriteUInt16LittleEndian(aux.Slice(12, 2), checked((ushort)section.Number));
        }

        int offset = 0;
        foreach (OutputSymbol symbol in symbols.Symbols)
        {
            Span<byte> record = table.Slice(offset, SymbolRecordSize);
            WriteName(record[..8], symbol.Name, stringTable, sectionHeader: false);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(8, 4), symbol.Value);
            BinaryPrimitives.WriteInt16LittleEndian(record.Slice(12, 2), symbol.SectionNumber);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(14, 2), symbol.Type);
            record[16] = symbol.StorageClass;
            record[17] = checked((byte)(symbol.Aux.Length / SymbolRecordSize));
            symbol.Aux.CopyTo(table.Slice(offset + SymbolRecordSize, symbol.Aux.Length));
            offset += SymbolRecordSize + symbol.Aux.Length;
        }
    }

    // A name of at most eight bytes is stored inline; a longer one goes to the string table,
    // referenced by a zero first word and its offset in a symbol record, or by "/<offset>" in a
    // section header.
    private static void WriteName(Span<byte> field, string name, MemoryStream stringTable, bool sectionHeader)
    {
        field.Clear();
        byte[] encoded = Encoding.ASCII.GetBytes(name);
        if (encoded.Length <= 8)
        {
            encoded.CopyTo(field);
            return;
        }

        int tableOffset = checked((int)stringTable.Position);
        stringTable.Write(encoded);
        stringTable.WriteByte(0);
        if (sectionHeader)
        {
            Encoding.ASCII.GetBytes($"/{tableOffset}").CopyTo(field);
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(field.Slice(4, 4), checked((uint)tableOffset));
    }
}
