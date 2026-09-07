namespace Ashes.Backend.Llvm;

/// <summary>
/// Merges several COFF relocatable objects into one, the way a relocatable link does: sections
/// with the same name and flags are concatenated at their alignment, every symbol (with its
/// auxiliary records) is rebased into the merged section it lands in, relocations keep their type
/// and move with their section, an undefined symbol one object references resolves to the
/// definition another object provides, and a COMDAT section several objects carry (the
/// floating-point constants LLVM emits as <c>__xmm@</c>/<c>__real@</c> sections) is kept once.
/// A relocation against an input section symbol is redirected to a static marker symbol whose
/// value is that section's offset in the merged section, so the addend stored in the relocated
/// bytes is never touched and the relocation types stay opaque; the same code therefore serves
/// the x64 and ARM64 Windows targets.
/// </summary>
internal static partial class CoffRelocatableObjects
{
    private const int FileHeaderSize = 20;
    private const int SectionHeaderSize = 40;
    private const int SymbolRecordSize = 18;
    private const int RelocationRecordSize = 10;
    private const int MaximumRelocationsPerSection = 0xFFFF;

    private const uint SectionAlignmentMask = 0x00F00000;
    private const int SectionAlignmentShift = 20;
    private const uint SectionUninitializedData = 0x00000080;
    private const uint SectionLinkInfo = 0x00000200;
    private const uint SectionLinkRemove = 0x00000800;
    private const uint SectionLinkComdat = 0x00001000;
    private const uint SectionRelocationOverflow = 0x01000000;

    private const byte StorageClassExternal = 2;
    private const byte StorageClassStatic = 3;
    private const byte StorageClassWeakExternal = 105;

    private const byte ComdatSelectNoDuplicates = 1;
    private const byte ComdatSelectAny = 2;
    private const byte ComdatSelectSameSize = 3;
    private const byte ComdatSelectExactMatch = 4;
    private const byte ComdatSelectAssociative = 5;
    private const byte ComdatSelectLargest = 6;

    /// <summary>Whether <see cref="Merge"/> is implemented.</summary>
    public static bool MergeSupported => true;

    public static byte[] Merge(IReadOnlyList<byte[]> objects)
    {
        if (objects.Count == 1)
        {
            return objects[0];
        }

        var inputs = new InputObject[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            inputs[i] = ParseInput(objects[i], i);
            if (inputs[i].Machine != inputs[0].Machine)
            {
                throw new InvalidOperationException(
                    $"COFF object {i} targets machine 0x{inputs[i].Machine:X4} but object 0 targets 0x{inputs[0].Machine:X4}.");
            }
        }

        ResolveComdatSections(inputs);
        List<OutputSection> sections = LayOutSections(inputs);
        var symbols = new OutputSymbolTable(sections);
        foreach (InputObject input in inputs)
        {
            symbols.MapSymbols(input);
        }

        symbols.ResolveWeakExternals();
        CollectRelocations(inputs);
        return WriteObject(inputs[0].Machine, sections, symbols);
    }

    // Decides which COMDAT section survives for each COMDAT name across all objects, in object
    // order, and marks the others dropped with a link to the survivor; an associative section is
    // dropped together with the section it is associated with.
    private static void ResolveComdatSections(InputObject[] inputs)
    {
        var survivors = new Dictionary<string, InputSection>(StringComparer.Ordinal);
        foreach (InputObject input in inputs)
        {
            foreach (InputSection section in input.Sections)
            {
                if (!section.IsComdat || section.ComdatSelection == ComdatSelectAssociative)
                {
                    continue;
                }

                string name = section.ComdatName
                    ?? throw new InvalidOperationException($"COFF object {input.Index} section {section.Number} ({section.Name}) is a COMDAT without a leader symbol.");
                if (!survivors.TryGetValue(name, out InputSection? kept))
                {
                    survivors[name] = section;
                    continue;
                }

                survivors[name] = SelectComdat(input, section, kept, name);
            }
        }

        foreach (InputObject input in inputs)
        {
            DropAssociatedSections(input);
        }
    }

    private static InputSection SelectComdat(InputObject input, InputSection candidate, InputSection kept, string name)
    {
        switch (candidate.ComdatSelection)
        {
            case ComdatSelectNoDuplicates:
                throw new InvalidOperationException($"COMDAT '{name}' is defined by COFF objects {kept.Owner.Index} and {input.Index} but forbids duplicates.");
            case ComdatSelectAny:
                candidate.DropFor(kept);
                return kept;
            case ComdatSelectSameSize:
                if (candidate.Size != kept.Size)
                {
                    throw new InvalidOperationException($"COMDAT '{name}' has size {candidate.Size} in COFF object {input.Index} but {kept.Size} in object {kept.Owner.Index}.");
                }

                candidate.DropFor(kept);
                return kept;
            case ComdatSelectExactMatch:
                if (!candidate.RawData.SequenceEqual(kept.RawData))
                {
                    throw new InvalidOperationException($"COMDAT '{name}' differs between COFF objects {kept.Owner.Index} and {input.Index}.");
                }

                candidate.DropFor(kept);
                return kept;
            case ComdatSelectLargest:
                if (candidate.Size > kept.Size)
                {
                    kept.DropFor(candidate);
                    return candidate;
                }

                candidate.DropFor(kept);
                return kept;
            default:
                throw new InvalidOperationException($"COMDAT '{name}' in COFF object {input.Index} uses unsupported selection {candidate.ComdatSelection}.");
        }
    }

    private static void DropAssociatedSections(InputObject input)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (InputSection section in input.Sections)
            {
                if (!section.IsComdat || section.ComdatSelection != ComdatSelectAssociative || section.Dropped)
                {
                    continue;
                }

                if (section.AssociatedSectionNumber < 1 || section.AssociatedSectionNumber > input.Sections.Length)
                {
                    throw new InvalidOperationException($"COFF object {input.Index} section {section.Number} is associated with a section that does not exist.");
                }

                if (input.Sections[section.AssociatedSectionNumber - 1].Dropped)
                {
                    section.DropFor(null);
                    changed = true;
                }
            }
        }
    }

    // Places every surviving section behind the earlier parts of the merged section with the
    // same name and flags, at the input section's own alignment; the merged section's alignment
    // is the largest of its parts'. Sections the linker discards (directives, address-significance
    // tables) are left out, and DWARF sections are rejected.
    private static List<OutputSection> LayOutSections(InputObject[] inputs)
    {
        var sections = new List<OutputSection>();
        var byKey = new Dictionary<string, OutputSection>(StringComparer.Ordinal);
        foreach (InputObject input in inputs)
        {
            foreach (InputSection section in input.Sections)
            {
                if (section.Dropped || (section.Characteristics & (SectionLinkInfo | SectionLinkRemove)) != 0)
                {
                    continue;
                }

                if (section.Name.StartsWith(".debug_", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"COFF object {input.Index} carries DWARF section {section.Name}; debug builds are linked from one object.");
                }

                uint flags = section.Characteristics & ~(SectionAlignmentMask | SectionLinkComdat | SectionRelocationOverflow);
                string key = $"{section.Name}\0{flags:X8}";
                if (!byKey.TryGetValue(key, out OutputSection? output))
                {
                    output = new OutputSection(sections.Count + 1, section.Name, flags);
                    byKey[key] = output;
                    sections.Add(output);
                }

                output.Append(section);
            }
        }

        return sections;
    }

    // Moves every surviving section's relocations into its merged section, rebased by the
    // section's offset and pointing at the merged symbol table.
    private static void CollectRelocations(InputObject[] inputs)
    {
        foreach (InputObject input in inputs)
        {
            foreach (InputSection section in input.Sections)
            {
                if (section.Dropped || section.Output is null)
                {
                    continue;
                }

                for (int i = 0; i < section.RelocationCount; i++)
                {
                    InputRelocation relocation = input.ReadRelocation(section, i);
                    if (relocation.SymbolIndex >= input.SymbolMap.Length || input.SymbolMap[relocation.SymbolIndex] < 0)
                    {
                        throw new InvalidOperationException(
                            $"COFF object {input.Index} section {section.Name} relocates against symbol {relocation.SymbolIndex}, which the merge discarded.");
                    }

                    section.Output.Relocations.Add(new OutputRelocation(
                        checked(relocation.VirtualAddress + section.Offset),
                        input.SymbolMap[relocation.SymbolIndex],
                        relocation.Type));
                }
            }
        }
    }

    private static uint AlignUp(uint value, uint alignment) =>
        alignment <= 1 ? value : checked((value + alignment - 1) & ~(alignment - 1));

    private static uint SectionAlignment(uint characteristics)
    {
        uint encoded = (characteristics & SectionAlignmentMask) >> SectionAlignmentShift;
        return encoded == 0 ? 1u : 1u << (int)(encoded - 1);
    }

    private static uint EncodeSectionAlignment(uint alignment)
    {
        uint encoded = 1;
        while ((1u << (int)(encoded - 1)) < alignment)
        {
            encoded++;
        }

        return encoded << SectionAlignmentShift;
    }
}
