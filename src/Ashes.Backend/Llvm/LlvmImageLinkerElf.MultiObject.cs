namespace Ashes.Backend.Llvm;

/// <summary>
/// Links several relocatable objects into one Linux x64 executable. The objects come from the
/// partitions of one program module: their text sections are laid out one after another behind
/// the entry trampoline, their allocated data sections one after another in the data segment,
/// and a function or global one object references as an undefined symbol resolves to the object
/// that defines it through a merged table of every object's global symbols. Only the first object
/// carries the entry symbol.
/// </summary>
internal static partial class LlvmImageLinker
{
    private const int MultiObjectSectionAlignment = 16;

    public static byte[] LinkLinuxExecutable(
        IReadOnlyList<byte[]> objects,
        string entrySymbolName,
        LinkedImagePayload? linkedPayload = null,
        IReadOnlyDictionary<string, string>? externalLibraries = null)
    {
        if (objects.Count == 1)
        {
            return LinkLinuxExecutable(objects[0], entrySymbolName, linkedPayload, externalLibraries);
        }

        ulong textVa = ElfBaseVa + (ulong)PageSize;
        ulong firstObjectTextVa = textVa + LinuxTrampolineLength;
        (ParsedElfObject[] parsed, ulong[] objectTextVas, int textLength) =
            LayoutMultiObjectText(objects, entrySymbolName, firstObjectTextVa);

        List<LinuxDynamicImport> imports = CollectMultiObjectDynamicImports(objects, parsed, externalLibraries);
        int textFileOffset = PageSize;
        int codeLength = LinuxTrampolineLength + textLength + imports.Count * LinuxImportStubLength;
        int dataFileOffset = Align(textFileOffset + codeLength, PageSize);
        ulong dataVa = ElfBaseVa + (ulong)dataFileOffset;
        (byte[] dataBytes, Dictionary<int, ulong>[] sectionBaseVas) = LayoutMultiObjectData(parsed, dataVa);

        int importDataOffset = Align(dataBytes.Length, 8);
        LinuxDynamicImportLayout importLayout = imports.Count == 0
            ? LinuxDynamicImportLayout.Empty
            : BuildLinuxDynamicImportLayout(imports, dataVa, checked((uint)importDataOffset), firstObjectTextVa + (ulong)textLength);

        var externalSymbolVas = new Dictionary<string, ulong>(importLayout.ImportStubVas, StringComparer.Ordinal);
        List<LinkerDataSegment> extraDataSegments = LinkLinuxExecutableCollectExtraDataSegments(
            linkedPayload, importLayout, importDataOffset, dataBytes.Length, dataVa, externalSymbolVas);
        for (int index = 0; index < objects.Count; index++)
        {
            foreach ((string name, ulong va) in BuildGlobalSymbolTable(objects[index], parsed[index], objectTextVas[index], sectionBaseVas[index]))
            {
                externalSymbolVas.TryAdd(name, va);
            }
        }

        for (int index = 0; index < objects.Count; index++)
        {
            LinkLinuxExecutableApplyRelocations(
                objects[index], parsed[index], dataBytes, dataVa, objectTextVas[index], sectionBaseVas[index], externalSymbolVas);
        }

        byte[] codeBytes = new byte[codeLength];
        byte[] trampoline = BuildLinuxTrampoline(parsed[0].EntryOffsetInText);
        Array.Copy(trampoline, 0, codeBytes, 0, trampoline.Length);
        for (int index = 0; index < objects.Count; index++)
        {
            Array.Copy(parsed[index].TextBytes, 0, codeBytes, checked((int)(objectTextVas[index] - textVa)), parsed[index].TextBytes.Length);
        }
        Array.Copy(importLayout.StubBytes, 0, codeBytes, LinuxTrampolineLength + textLength, importLayout.StubBytes.Length);

        byte[] finalDataBytes = extraDataSegments.Count == 0
            ? dataBytes
            : BuildLinuxDataBytes(dataBytes, extraDataSegments);
        return LinkLinuxExecutableEmitImage(
            parsed[0], imports.Count > 0, importLayout, codeBytes, finalDataBytes,
            textVa, textFileOffset, dataFileOffset, dataVa);
    }

    // Parses every object and assigns each text section its place behind the trampoline.
    private static (ParsedElfObject[] Parsed, ulong[] ObjectTextVas, int TextLength) LayoutMultiObjectText(
        IReadOnlyList<byte[]> objects,
        string entrySymbolName,
        ulong firstObjectTextVa)
    {
        var parsed = new ParsedElfObject[objects.Count];
        var objectTextVas = new ulong[objects.Count];
        int textLength = 0;
        for (int index = 0; index < objects.Count; index++)
        {
            parsed[index] = ParseElfObject(objects[index], index == 0 ? entrySymbolName : null);
            if (parsed[index].DebugSections.Count > 0 || parsed[index].TlsSections.Count > 0)
            {
                throw new InvalidOperationException("A multi-object link carries neither debug nor TLS sections; such programs are linked from one object.");
            }

            textLength = Align(textLength, MultiObjectSectionAlignment);
            objectTextVas[index] = firstObjectTextVa + (ulong)textLength;
            textLength += parsed[index].TextBytes.Length;
        }

        return (parsed, objectTextVas, textLength);
    }

    // The union of every object's dynamic imports, one stub per symbol name, numbered in name
    // order as the dynamic symbol table expects.
    private static List<LinuxDynamicImport> CollectMultiObjectDynamicImports(
        IReadOnlyList<byte[]> objects,
        ParsedElfObject[] parsed,
        IReadOnlyDictionary<string, string>? externalLibraries)
    {
        var libraries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < objects.Count; index++)
        {
            foreach (LinuxDynamicImport import in CollectLinuxDynamicImports(objects[index], parsed[index], externalLibraries))
            {
                libraries.TryAdd(import.SymbolName, import.LibraryName);
            }
        }

        var imports = new List<LinuxDynamicImport>(libraries.Count);
        foreach ((string symbolName, string libraryName) in libraries)
        {
            imports.Add(new LinuxDynamicImport(symbolName, libraryName, imports.Count + 1));
        }

        return imports;
    }

    // Lays out every object's allocated sections in turn and returns each object's section bases.
    private static (byte[] DataBytes, Dictionary<int, ulong>[] SectionBaseVas) LayoutMultiObjectData(
        ParsedElfObject[] parsed,
        ulong dataVa)
    {
        using var data = new MemoryStream();
        var sectionBaseVas = new Dictionary<int, ulong>[parsed.Length];
        for (int index = 0; index < parsed.Length; index++)
        {
            int aligned = Align(checked((int)data.Position), MultiObjectSectionAlignment);
            while (data.Position < aligned)
            {
                data.WriteByte(0);
            }

            LaidOutElfSections laidOut = LayoutElfAllocatedSections(parsed[index].AllocatedSections, dataVa + (ulong)data.Position);
            data.Write(laidOut.DataBytes);
            sectionBaseVas[index] = laidOut.SectionBaseVas;
        }

        return (data.ToArray(), sectionBaseVas);
    }

    // The object's defined global and weak symbols with their final addresses; local symbols stay
    // private to the object's own relocation resolution.
    private static Dictionary<string, ulong> BuildGlobalSymbolTable(
        byte[] objectBytes,
        ParsedElfObject parsed,
        ulong loadedTextVa,
        IReadOnlyDictionary<int, ulong> sectionBaseVas)
    {
        ReadOnlySpan<byte> bytes = objectBytes;
        ElfSectionHeader symtab = parsed.SymbolTable;
        if (symtab.EntrySize == 0)
        {
            throw new InvalidOperationException("LLVM symbol table is missing entry size metadata.");
        }

        byte[] strtab = ReadStringTable(bytes, ReadElfSectionHeader(bytes, (int)symtab.Link));
        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        int symbolCount = checked((int)(symtab.Size / symtab.EntrySize));
        for (int index = 0; index < symbolCount; index++)
        {
            int offset = checked((int)symtab.Offset + index * (int)symtab.EntrySize);
            byte binding = (byte)(bytes[offset + 4] >> 4);
            if (binding == ElfSymbolBindingLocal)
            {
                continue;
            }

            ElfSymbol symbol = ReadElfSymbol(bytes, symtab, index);
            if (symbol.SectionIndex == 0)
            {
                continue;
            }

            string name = ReadNullTerminatedString(strtab, (int)symbol.NameIndex);
            if (name.Length == 0)
            {
                continue;
            }

            if (symbol.SectionIndex == parsed.TextSectionIndex)
            {
                result.TryAdd(name, loadedTextVa + symbol.Value);
            }
            else if (sectionBaseVas.TryGetValue(symbol.SectionIndex, out ulong baseVa))
            {
                result.TryAdd(name, baseVa + symbol.Value);
            }
        }

        return result;
    }

    private const byte ElfSymbolBindingLocal = 0;
}
