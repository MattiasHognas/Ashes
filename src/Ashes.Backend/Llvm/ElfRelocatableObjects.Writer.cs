using System.Buffers.Binary;
using System.Text;

namespace Ashes.Backend.Llvm;

internal static partial class ElfRelocatableObjects
{
    // A section header of the merged object with its contents; NOBITS sections carry none.
    private sealed record OutputHeader(
        string Name,
        uint Type,
        ulong Flags,
        uint Link,
        uint Info,
        ulong Alignment,
        ulong EntrySize)
    {
        public ulong Size { get; set; }

        public byte[]? Contents { get; set; }

        public ulong FileOffset { get; set; }

        public uint NameOffset { get; set; }
    }

    private sealed partial class MergeState
    {
        // Lays the merged object out as the merged sections, one .rela.<name> section per merged
        // section with relocations, then .symtab, .strtab, and .shstrtab, followed by the
        // section header table.
        public byte[] Write(InputObject first)
        {
            List<OutputHeader> headers = BuildContentHeaders();
            int symtabIndex = 1 + _sections.Count + _sections.Count(static section => section.Relocations.Length != 0);
            AppendRelocationHeaders(headers, symtabIndex);
            (byte[] strtab, byte[] symtab) = BuildSymbolTables();
            headers.Add(WithContents(new OutputHeader(".symtab", SectionTypeSymtab, 0, checked((uint)(symtabIndex + 1)), checked((uint)(1 + _locals.Count)), 8, SymbolSize), symtab));
            headers.Add(WithContents(new OutputHeader(".strtab", SectionTypeStrtab, 0, 0, 0, 1, 0), strtab));
            var sectionNames = new OutputHeader(".shstrtab", SectionTypeStrtab, 0, 0, 0, 1, 0);
            headers.Add(sectionNames);
            WithContents(sectionNames, BuildSectionNameTable(headers));

            ulong position = ElfHeaderSize;
            foreach (OutputHeader header in headers)
            {
                position = AlignUp(position, header.Alignment);
                header.FileOffset = position;
                if (header.Contents is not null)
                {
                    position += header.Size;
                }
            }

            ulong sectionHeaderOffset = AlignUp(position, 8);
            int sectionCount = headers.Count + 1;
            byte[] output = new byte[checked((int)sectionHeaderOffset + sectionCount * SectionHeaderSize)];
            WriteElfHeader(output, first, sectionHeaderOffset, sectionCount, checked((ushort)headers.Count));
            for (int index = 0; index < headers.Count; index++)
            {
                OutputHeader header = headers[index];
                header.Contents?.CopyTo(output.AsSpan(checked((int)header.FileOffset)));
                WriteSectionHeader(output.AsSpan(checked((int)sectionHeaderOffset + (index + 1) * SectionHeaderSize), SectionHeaderSize), header);
            }

            return output;
        }

        private List<OutputHeader> BuildContentHeaders()
        {
            var headers = new List<OutputHeader>(_sections.Count * 2 + 3);
            foreach (OutputSection section in _sections)
            {
                var header = new OutputHeader(section.Name, section.Type, section.Flags, 0, 0, section.Alignment, section.EntrySize)
                {
                    Size = section.Size,
                    Contents = section.Type == SectionTypeNoBits ? null : section.Bytes.ToArray(),
                };
                headers.Add(header);
            }

            return headers;
        }

        private static OutputHeader WithContents(OutputHeader header, byte[] contents)
        {
            header.Contents = contents;
            header.Size = (ulong)contents.Length;
            return header;
        }

        private void AppendRelocationHeaders(List<OutputHeader> headers, int symtabIndex)
        {
            foreach (OutputSection section in _sections)
            {
                if (section.Relocations.Length == 0)
                {
                    continue;
                }

                headers.Add(WithContents(
                    new OutputHeader(".rela" + section.Name, SectionTypeRela, SectionFlagInfoLink, checked((uint)symtabIndex), checked((uint)section.Index), 8, RelaSize),
                    section.Relocations.ToArray()));
            }
        }

        // The string table (index 0 empty) and the symbol table: the null symbol, every local, then
        // every global.
        private (byte[] Strtab, byte[] Symtab) BuildSymbolTables()
        {
            using var strtab = new MemoryStream();
            strtab.WriteByte(0);
            byte[] symtab = new byte[(1 + _locals.Count + _globals.Count) * SymbolSize];
            int position = SymbolSize;
            foreach (OutputSymbol symbol in _locals.Concat(_globals))
            {
                uint nameOffset = 0;
                if (symbol.Name.Length != 0)
                {
                    nameOffset = checked((uint)strtab.Length);
                    strtab.Write(Encoding.UTF8.GetBytes(symbol.Name));
                    strtab.WriteByte(0);
                }

                Span<byte> entry = symtab.AsSpan(position, SymbolSize);
                BinaryPrimitives.WriteUInt32LittleEndian(entry, nameOffset);
                entry[4] = symbol.Info;
                entry[5] = symbol.Other;
                BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(6, 2), symbol.SectionIndex);
                BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(8, 8), symbol.Value);
                BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(16, 8), symbol.Size);
                position += SymbolSize;
            }

            return (strtab.ToArray(), symtab);
        }

        private static byte[] BuildSectionNameTable(List<OutputHeader> headers)
        {
            using var table = new MemoryStream();
            table.WriteByte(0);
            foreach (OutputHeader header in headers)
            {
                header.NameOffset = checked((uint)table.Length);
                table.Write(Encoding.UTF8.GetBytes(header.Name));
                table.WriteByte(0);
            }

            return table.ToArray();
        }

        private static void WriteElfHeader(byte[] output, InputObject first, ulong sectionHeaderOffset, int sectionCount, ushort sectionNamesIndex)
        {
            Span<byte> header = output.AsSpan(0, ElfHeaderSize);
            first.Bytes.AsSpan(0, 16).CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(16, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(18, 2), first.Machine);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(20, 4), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(24, 8), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(32, 8), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(40, 8), sectionHeaderOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(48, 4), first.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(52, 2), ElfHeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(54, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(56, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(58, 2), SectionHeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(60, 2), checked((ushort)sectionCount));
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(62, 2), sectionNamesIndex);
        }

        private static void WriteSectionHeader(Span<byte> entry, OutputHeader header)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(entry, header.NameOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(4, 4), header.Type);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(8, 8), header.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(16, 8), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(24, 8), header.FileOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(32, 8), header.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(40, 4), header.Link);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(44, 4), header.Info);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(48, 8), header.Alignment);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(56, 8), header.EntrySize);
        }
    }
}
