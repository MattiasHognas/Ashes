using System.Buffers.Binary;

namespace Ashes.Backend.Llvm;

internal static partial class ElfRelocatableObjects
{
    // A section of the merged object: the concatenation of every input section of its name, with
    // the relocations that target it and the local section symbol relocations refer to it by.
    private sealed class OutputSection
    {
        public OutputSection(int index, string name, uint type, ulong flags, ulong entrySize)
        {
            Index = index;
            Name = name;
            Type = type;
            Flags = flags;
            EntrySize = entrySize;
        }

        public int Index { get; }

        public string Name { get; }

        public uint Type { get; }

        public ulong Flags { get; set; }

        public ulong EntrySize { get; set; }

        public ulong Alignment { get; private set; } = 1;

        public ulong Size { get; private set; }

        public MemoryStream Bytes { get; } = new();

        public MemoryStream Relocations { get; } = new();

        public int SectionSymbolIndex { get; set; }

        // Appends one input section's contents at the next offset its alignment allows and
        // returns that offset.
        public ulong Append(SectionHeader header, ReadOnlySpan<byte> bytes)
        {
            ulong alignment = Math.Max(1UL, header.AddressAlign);
            Alignment = Math.Max(Alignment, alignment);
            ulong offset = AlignUp(Size, alignment);
            if (Type != SectionTypeNoBits)
            {
                Bytes.SetLength(checked((long)offset));
                Bytes.Position = Bytes.Length;
                Bytes.Write(bytes);
            }

            Size = offset + header.Size;
            return offset;
        }
    }

    private sealed partial class MergeState
    {
        private readonly List<OutputSection> _sections = [];
        private readonly Dictionary<string, OutputSection> _sectionsByName = new(StringComparer.Ordinal);
        private readonly List<OutputSymbol> _locals = [];
        private readonly List<OutputSymbol> _globals = [];
        private readonly Dictionary<string, int> _globalSlots = new(StringComparer.Ordinal);
        private readonly List<(InputObject Input, int SymbolIndex, int Slot)> _pendingGlobals = [];

        public void PlaceSections(InputObject input)
        {
            for (int index = 1; index < input.Sections.Length; index++)
            {
                SectionHeader header = input.Sections[index];
                string name = input.SectionNames[index];
                if (!IsMergedSection(header, name))
                {
                    continue;
                }

                OutputSection output = GetOrAddSection(name, header);
                input.SectionOffsets[index] = output.Append(header, input.SectionBytes(header));
                input.SectionMap[index] = output.Index;
            }
        }

        // Every merged section gets one local section symbol, which the inputs' section symbols
        // map to.
        public void AddSectionSymbols()
        {
            foreach (OutputSection section in _sections)
            {
                section.SectionSymbolIndex = _locals.Count + 1;
                _locals.Add(new OutputSymbol(string.Empty, (SymbolBindingLocal << 4) | SymbolTypeSection, 0, checked((ushort)section.Index), 0, 0));
            }
        }

        public void CollectLocalSymbols(InputObject input)
        {
            for (int index = 1; index < input.SymbolCount; index++)
            {
                Symbol symbol = input.ReadSymbol(index);
                if (symbol.Binding != SymbolBindingLocal)
                {
                    continue;
                }

                if (symbol.Type == SymbolTypeSection)
                {
                    int outputSection = symbol.SectionIndex < input.SectionMap.Length ? input.SectionMap[symbol.SectionIndex] : -1;
                    if (outputSection >= 0)
                    {
                        input.SymbolMap[index] = _sections[outputSection - 1].SectionSymbolIndex;
                        input.SectionSymbolAddends[index] = input.SectionOffsets[symbol.SectionIndex];
                    }

                    continue;
                }

                OutputSymbol? rebased = Rebase(input, symbol);
                if (rebased is not null)
                {
                    _locals.Add(rebased);
                    input.SymbolMap[index] = _locals.Count;
                }
            }
        }

        public void CollectGlobalSymbols(InputObject input)
        {
            for (int index = 1; index < input.SymbolCount; index++)
            {
                Symbol symbol = input.ReadSymbol(index);
                if (symbol.Binding == SymbolBindingLocal)
                {
                    continue;
                }

                if (symbol.Binding is not (SymbolBindingGlobal or SymbolBindingWeak))
                {
                    throw new InvalidOperationException($"ELF symbol '{input.SymbolName(symbol)}' has unsupported binding {symbol.Binding}.");
                }

                OutputSymbol rebased = Rebase(input, symbol)
                    ?? throw new InvalidOperationException($"ELF global symbol '{input.SymbolName(symbol)}' lives in a section the merge drops.");
                if (_globalSlots.TryGetValue(rebased.Name, out int slot))
                {
                    _globals[slot] = ResolveGlobal(_globals[slot], rebased);
                }
                else
                {
                    slot = _globals.Count;
                    _globals.Add(rebased);
                    _globalSlots[rebased.Name] = slot;
                }

                _pendingGlobals.Add((input, index, slot));
            }
        }

        // Global symbol indices follow every object's locals, so they are known only once all the
        // locals are collected.
        public void AssignGlobalSymbolIndices()
        {
            foreach ((InputObject input, int symbolIndex, int slot) in _pendingGlobals)
            {
                input.SymbolMap[symbolIndex] = 1 + _locals.Count + slot;
            }
        }

        public void CollectRelocations(InputObject input)
        {
            for (int index = 1; index < input.Sections.Length; index++)
            {
                SectionHeader header = input.Sections[index];
                if (header.Type != SectionTypeRela)
                {
                    continue;
                }

                if (header.Link != (uint)input.SymtabIndex || header.EntrySize != RelaSize)
                {
                    throw new InvalidOperationException($"ELF relocation section '{input.SectionNames[index]}' has unexpected metadata.");
                }

                int targetSection = header.Info < (uint)input.SectionMap.Length ? input.SectionMap[(int)header.Info] : -1;
                if (targetSection < 0)
                {
                    throw new InvalidOperationException($"ELF relocation section '{input.SectionNames[index]}' targets a section the merge drops.");
                }

                AppendRelocations(input, header, _sections[targetSection - 1], input.SectionOffsets[(int)header.Info]);
            }
        }

        private static void AppendRelocations(InputObject input, SectionHeader header, OutputSection target, ulong targetOffset)
        {
            ReadOnlySpan<byte> bytes = input.SectionBytes(header);
            int count = bytes.Length / RelaSize;
            Span<byte> entry = stackalloc byte[RelaSize];
            for (int index = 0; index < count; index++)
            {
                ReadOnlySpan<byte> source = bytes.Slice(index * RelaSize, RelaSize);
                ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(source);
                ulong info = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(8, 8));
                long addend = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(16, 8));
                int symbolIndex = checked((int)(info >> 32));
                int mappedSymbol = symbolIndex < input.SymbolMap.Length ? input.SymbolMap[symbolIndex] : -1;
                if (mappedSymbol < 0)
                {
                    throw new InvalidOperationException($"ELF relocation in '{target.Name}' references symbol {symbolIndex}, which the merge dropped.");
                }

                BinaryPrimitives.WriteUInt64LittleEndian(entry, checked(offset + targetOffset));
                BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(8, 8), ((ulong)mappedSymbol << 32) | (info & 0xFFFFFFFFUL));
                BinaryPrimitives.WriteInt64LittleEndian(entry.Slice(16, 8), checked(addend + (long)input.SectionSymbolAddends[symbolIndex]));
                target.Relocations.Write(entry);
            }
        }

        // Whether a section's contents are carried into the merged object. Symbol, string, and
        // relocation tables are rebuilt; the LLVM address-significance table indexes the symbol
        // table, so it is dropped; debug sections are not merged. Processor-specific section
        // types (the x86-64 unwind type of .eh_frame) are concatenated like PROGBITS.
        private static bool IsMergedSection(SectionHeader header, string name)
        {
            if (name.StartsWith(".debug", StringComparison.Ordinal) || name.StartsWith(".rela.debug", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"ELF objects carrying the debug section '{name}' cannot be merged.");
            }

            if (header.Type is >= SectionTypeLowProcessor and <= SectionTypeHighProcessor)
            {
                return true;
            }

            switch (header.Type)
            {
                case SectionTypeSymtab:
                case SectionTypeStrtab:
                case SectionTypeRela:
                case SectionTypeLlvmAddrsig:
                    return false;
                case SectionTypeRel:
                    throw new InvalidOperationException($"ELF relocation section '{name}' without addends cannot be merged.");
                case SectionTypeProgBits:
                case SectionTypeNoBits:
                case SectionTypeNote:
                    return true;
                default:
                    throw new InvalidOperationException($"ELF section '{name}' of type {header.Type} cannot be merged.");
            }
        }

        private OutputSection GetOrAddSection(string name, SectionHeader header)
        {
            if (!_sectionsByName.TryGetValue(name, out OutputSection? section))
            {
                section = new OutputSection(_sections.Count + 1, name, header.Type, header.Flags, header.EntrySize);
                _sections.Add(section);
                _sectionsByName[name] = section;
                return section;
            }

            if (section.Type != header.Type || section.Flags != header.Flags)
            {
                throw new InvalidOperationException($"ELF section '{name}' has different types or flags across the merged objects.");
            }

            if (section.EntrySize != header.EntrySize)
            {
                section.EntrySize = 0;
                section.Flags &= ~SectionFlagMerge;
            }

            return section;
        }

        // The symbol with its section index and value moved into the merged object, or null when
        // its section was dropped.
        private static OutputSymbol? Rebase(InputObject input, Symbol symbol)
        {
            string name = input.SymbolName(symbol);
            if (symbol.SectionIndex == 0 || symbol.SectionIndex >= SectionIndexLowReserved)
            {
                if (symbol.SectionIndex == SectionIndexCommon)
                {
                    throw new InvalidOperationException($"ELF common symbol '{name}' cannot be merged.");
                }

                return new OutputSymbol(name, symbol.Info, symbol.Other, symbol.SectionIndex, symbol.Value, symbol.Size);
            }

            if (symbol.SectionIndex >= input.SectionMap.Length || input.SectionMap[symbol.SectionIndex] < 0)
            {
                return null;
            }

            return new OutputSymbol(
                name,
                symbol.Info,
                symbol.Other,
                checked((ushort)input.SectionMap[symbol.SectionIndex]),
                checked(symbol.Value + input.SectionOffsets[symbol.SectionIndex]),
                symbol.Size);
        }

        // A definition beats an undefined reference, a global definition beats a weak one, and
        // two definitions of the same global name are an error.
        private static OutputSymbol ResolveGlobal(OutputSymbol existing, OutputSymbol candidate)
        {
            bool existingDefined = existing.SectionIndex != 0;
            bool candidateDefined = candidate.SectionIndex != 0;
            if (!candidateDefined)
            {
                return existing;
            }

            if (!existingDefined)
            {
                return candidate;
            }

            byte existingBinding = (byte)(existing.Info >> 4);
            byte candidateBinding = (byte)(candidate.Info >> 4);
            if (existingBinding == SymbolBindingWeak && candidateBinding == SymbolBindingGlobal)
            {
                return candidate;
            }

            if (candidateBinding == SymbolBindingWeak)
            {
                return existing;
            }

            throw new InvalidOperationException($"ELF symbol '{existing.Name}' is defined in more than one of the merged objects.");
        }
    }
}
