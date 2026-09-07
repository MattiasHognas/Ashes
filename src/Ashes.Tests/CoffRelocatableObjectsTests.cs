using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Ashes.Backend.Backends;
using Ashes.Backend.Llvm;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// The Windows targets link a program split into several partitions by merging the partitions'
// COFF objects into one relocatable object first. The end-to-end tests compile with three
// partitions; the merge itself is exercised on hand-built objects so each rule (section
// concatenation, symbol rebasing, section-symbol markers, COMDAT folding, duplicate externals,
// the relocation overflow form) is checked in isolation.
public sealed class CoffRelocatableObjectsTests
{
    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineArm64 = 0xAA64;
    private const uint TextCharacteristics = 0x60500020; // code, execute, read, align 16
    private const uint RdataCharacteristics = 0x40300040; // initialized data, read, align 4
    private const uint BssCharacteristics = 0xC0500080; // uninitialized data, read, write, align 16
    private const uint ComdatFlag = 0x00001000;
    private const uint RelocationOverflowFlag = 0x01000000;
    private const ushort RelocAddr64 = 0x0001;
    private const ushort RelocRel32 = 0x0004;
    private const byte ClassExternal = 2;
    private const byte ClassStatic = 3;
    private const byte ClassWeakExternal = 105;

    [Test]
    public async Task Windows_x64_program_split_into_three_objects_runs_under_wine()
    {
        if (!TestProcessHelper.CanRunWindowsExecutables())
        {
            return;
        }

        // The partitions share the runtime globals and the string literals, call each other's
        // functions by name, and one of them holds the entry; the float arithmetic pulls the
        // constant COMDAT sections LLVM emits per module, which the merge must fold.
        const string source = """
            import Ashes.IO
            import Ashes.Text
            let parts = Ashes.Text.split("GET / HTTP/1.1")(" ")
            in match parts with
                | method :: path :: version :: _rest ->
                    Ashes.IO.print(method + "|" + path + "|" + version + "|" + Ashes.Text.fromInt(Ashes.Text.byteLength(path)))
                | _other -> Ashes.IO.print("bad")
            """;
        IrProgram program = IrOptimizer.Optimize(LowerProgramWithImports(source));
        byte[] image = new WindowsX64LlvmBackend().Compile(program, BackendCompileOptions.Default with { ObjectPartitions = 3 });

        (string stdout, int exitCode) = await RunWindowsExecutableAsync(image).ConfigureAwait(false);

        exitCode.ShouldBe(0);
        stdout.ShouldBe("GET|/|HTTP/1.1|1\n");
    }

    [Test]
    public void Windows_arm64_program_split_into_three_objects_links_an_ARM64_image()
    {
        const string source = """
            import Ashes.IO
            import Ashes.Text
            let classify = given (n) ->
                match n with
                    | 0 -> "zero"
                    | 1 -> "one"
                    | _ -> "many"
            in Ashes.IO.print(classify(Ashes.Text.byteLength("ab")) + Ashes.Text.fromInt(Ashes.Text.byteLength("abc")))
            """;
        IrProgram program = IrOptimizer.Optimize(LowerProgramWithImports(source));

        byte[] image = new WindowsArm64LlvmBackend().Compile(program, BackendCompileOptions.Default with { ObjectPartitions = 3 });

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(60, 4));
        image[peOffset].ShouldBe((byte)'P');
        image[peOffset + 1].ShouldBe((byte)'E');
        BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(peOffset + 4, 2)).ShouldBe(MachineArm64);
        int optionalHeader = peOffset + 24;
        BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(optionalHeader, 2)).ShouldBe((ushort)0x020B);
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optionalHeader + 16, 4)).ShouldBeGreaterThan(0u);
        int dataDirectories = optionalHeader + 112;
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(dataDirectories + 8, 4)).ShouldBeGreaterThan(0u);
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(dataDirectories + 12, 4)).ShouldBeGreaterThan(0u);
    }

    [Test]
    public void Merge_concatenates_sections_and_rebases_symbols_and_relocations()
    {
        var first = new CoffBuilder();
        int firstText = first.AddSection(".text", TextCharacteristics, new byte[20]);
        first.AddSection(".rdata", RdataCharacteristics, new byte[6]);
        first.AddSymbol("alpha", 4, firstText, ClassExternal);
        int firstBeta = first.AddSymbol("beta", 0, 0, ClassExternal);
        first.AddRelocation(firstText, 8, firstBeta, RelocRel32);

        var second = new CoffBuilder();
        int secondText = second.AddSection(".text", TextCharacteristics, new byte[10]);
        int secondRdata = second.AddSection(".rdata", RdataCharacteristics, new byte[8]);
        second.AddSymbol("beta", 2, secondText, ClassExternal);
        int secondAlpha = second.AddSymbol("alpha", 0, 0, ClassExternal);
        int secondRdataSymbol = second.SectionSymbolIndex(secondRdata);
        second.AddRelocation(secondText, 1, secondAlpha, RelocRel32);
        second.AddRelocation(secondText, 5, secondRdataSymbol, RelocAddr64);

        ParsedCoff merged = ParsedCoff.Parse(CoffRelocatableObjects.Merge([first.Build(MachineAmd64), second.Build(MachineAmd64)]));

        merged.Machine.ShouldBe(MachineAmd64);
        merged.Sections.Select(static section => section.Name).ToArray().ShouldBe(new[] { ".text", ".rdata" });
        merged.Sections[0].Size.ShouldBe(42u); // 20, padded to the 16-byte alignment, then 10
        merged.Sections[1].Size.ShouldBe(16u); // 6, padded to 4, then 8
        ParsedSymbol alpha = merged.Symbols.Single(static symbol => string.Equals(symbol.Name, "alpha", StringComparison.Ordinal));
        ParsedSymbol beta = merged.Symbols.Single(static symbol => string.Equals(symbol.Name, "beta", StringComparison.Ordinal));
        (alpha.SectionNumber, alpha.Value, alpha.StorageClass).ShouldBe(((short)1, 4u, ClassExternal));
        (beta.SectionNumber, beta.Value, beta.StorageClass).ShouldBe(((short)1, 34u, ClassExternal));
        merged.Sections[0].Relocations.Select(static relocation => (relocation.VirtualAddress, relocation.Type))
            .ToArray().ShouldBe(new[] { (8u, RelocRel32), (33u, RelocRel32), (37u, RelocAddr64) });
        merged.SymbolAt(merged.Sections[0].Relocations[0].SymbolIndex).Name.ShouldBe("beta");
        merged.SymbolAt(merged.Sections[0].Relocations[1].SymbolIndex).Name.ShouldBe("alpha");
        ParsedSymbol marker = merged.SymbolAt(merged.Sections[0].Relocations[2].SymbolIndex);
        (marker.Name, marker.SectionNumber, marker.Value, marker.StorageClass).ShouldBe((".rdata", (short)2, 8u, ClassStatic));
    }

    [Test]
    public void Merge_keeps_one_copy_of_a_comdat_and_demotes_duplicate_runtime_helpers()
    {
        const string constant = "__xmm@80000000000000008000000000000000";
        var first = new CoffBuilder();
        int firstText = first.AddSection(".text", TextCharacteristics, new byte[16]);
        int firstConstant = first.AddSection(".rdata", RdataCharacteristics | ComdatFlag, new byte[16], comdatSelection: 2);
        first.AddSymbol(constant, 0, firstConstant, ClassExternal);
        first.AddSymbol("memcpy", 0, firstText, ClassExternal);
        first.AddSection(".bss", (BssCharacteristics & ~0x00F00000u) | 0x00300000u, new byte[24], uninitialized: true);

        var second = new CoffBuilder();
        int secondText = second.AddSection(".text", TextCharacteristics, new byte[16]);
        int secondConstant = second.AddSection(".rdata", RdataCharacteristics | ComdatFlag, new byte[16], comdatSelection: 2);
        int secondConstantSymbol = second.AddSymbol(constant, 0, secondConstant, ClassExternal);
        int secondMemcpy = second.AddSymbol("memcpy", 0, secondText, ClassExternal);
        second.AddRelocation(secondText, 2, secondConstantSymbol, RelocRel32);
        second.AddRelocation(secondText, 9, secondMemcpy, RelocRel32);
        second.AddSection(".bss", BssCharacteristics, new byte[4], uninitialized: true);

        ParsedCoff merged = ParsedCoff.Parse(CoffRelocatableObjects.Merge([first.Build(MachineAmd64), second.Build(MachineAmd64)]));

        merged.Sections.Select(static section => section.Name).ToArray().ShouldBe(new[] { ".text", ".rdata", ".bss" });
        merged.Sections[1].Size.ShouldBe(16u);
        (merged.Sections[1].Characteristics & ComdatFlag).ShouldBe(0u);
        merged.Sections[2].Size.ShouldBe(36u); // 24 at alignment 4, then 4 at alignment 16, which the merged section takes
        (merged.Sections[2].Characteristics & 0x00F00000u).ShouldBe(0x00500000u);
        merged.Sections[2].PointerToRawData.ShouldBe(0u);
        merged.Symbols.Count(symbol => string.Equals(symbol.Name, constant, StringComparison.Ordinal)).ShouldBe(1);
        List<ParsedSymbol> memcpys = merged.Symbols.Where(static symbol => string.Equals(symbol.Name, "memcpy", StringComparison.Ordinal)).ToList();
        memcpys.Select(static symbol => (symbol.StorageClass, symbol.Value)).ToArray().ShouldBe(new[] { (ClassExternal, 0u), (ClassStatic, 16u) });
        merged.SymbolAt(merged.Sections[0].Relocations[0].SymbolIndex).ShouldBe(merged.Symbols.Single(symbol => string.Equals(symbol.Name, constant, StringComparison.Ordinal)));
        merged.SymbolAt(merged.Sections[0].Relocations[1].SymbolIndex).ShouldBe(memcpys[1]);
    }

    [Test]
    public void Merge_resolves_weak_externals_to_a_strong_definition_or_their_alternate()
    {
        // LLVM lowers a weak function on COFF to a weak external whose alternate is a plain
        // function; the first object defines strlen strongly, none defines memcmp.
        var first = new CoffBuilder();
        int firstText = first.AddSection(".text", TextCharacteristics, new byte[16]);
        first.AddSymbol("strlen", 4, firstText, ClassExternal);
        int firstMemcmpDefault = first.AddSymbol(".weak.memcmp.default.a", 8, firstText, ClassExternal);
        first.AddSymbol("memcmp", 0, 0, ClassWeakExternal, WeakExternalAux(firstMemcmpDefault));

        var second = new CoffBuilder();
        int secondText = second.AddSection(".text", TextCharacteristics, new byte[16]);
        int secondStrlenDefault = second.AddSymbol(".weak.strlen.default.b", 0, secondText, ClassExternal);
        int secondStrlen = second.AddSymbol("strlen", 0, 0, ClassWeakExternal, WeakExternalAux(secondStrlenDefault));
        int secondMemcmpDefault = second.AddSymbol(".weak.memcmp.default.b", 8, secondText, ClassExternal);
        int secondMemcmp = second.AddSymbol("memcmp", 0, 0, ClassWeakExternal, WeakExternalAux(secondMemcmpDefault));
        second.AddRelocation(secondText, 1, secondStrlen, RelocRel32);
        second.AddRelocation(secondText, 5, secondMemcmp, RelocRel32);

        ParsedCoff merged = ParsedCoff.Parse(CoffRelocatableObjects.Merge([first.Build(MachineAmd64), second.Build(MachineAmd64)]));

        ParsedSymbol strlen = merged.Symbols.Single(static symbol => string.Equals(symbol.Name, "strlen", StringComparison.Ordinal));
        (strlen.SectionNumber, strlen.Value, strlen.StorageClass).ShouldBe(((short)1, 4u, ClassExternal));
        ParsedSymbol memcmp = merged.Symbols.Single(static symbol => string.Equals(symbol.Name, "memcmp", StringComparison.Ordinal));
        (memcmp.SectionNumber, memcmp.Value, memcmp.StorageClass).ShouldBe(((short)1, 8u, ClassExternal));
        merged.SymbolAt(merged.Sections[0].Relocations[0].SymbolIndex).ShouldBe(strlen);
        merged.SymbolAt(merged.Sections[0].Relocations[1].SymbolIndex).ShouldBe(memcmp);
        merged.Symbols.Count(static symbol => symbol.StorageClass == ClassWeakExternal).ShouldBe(0);
    }

    [Test]
    public void Merge_writes_the_overflow_form_past_65535_relocations()
    {
        var first = new CoffBuilder();
        int firstText = first.AddSection(".text", TextCharacteristics, new byte[40000]);
        int target = first.AddSymbol("target", 0, firstText, ClassExternal);
        for (int i = 0; i < 40000; i++)
        {
            first.AddRelocation(firstText, (uint)i, target, RelocRel32);
        }

        var second = new CoffBuilder();
        int secondText = second.AddSection(".text", TextCharacteristics, new byte[30000]);
        int reference = second.AddSymbol("target", 0, 0, ClassExternal);
        for (int i = 0; i < 30000; i++)
        {
            second.AddRelocation(secondText, (uint)i, reference, RelocRel32);
        }

        byte[] mergedBytes = CoffRelocatableObjects.Merge([first.Build(MachineAmd64), second.Build(MachineAmd64)]);

        ReadOnlySpan<byte> header = mergedBytes.AsSpan(20, 40);
        BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(32, 2)).ShouldBe((ushort)0xFFFF);
        (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(36, 4)) & RelocationOverflowFlag).ShouldBe(RelocationOverflowFlag);
        int relocations = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24, 4)));
        BinaryPrimitives.ReadUInt32LittleEndian(mergedBytes.AsSpan(relocations, 4)).ShouldBe(70001u);
        ParsedCoff merged = ParsedCoff.Parse(mergedBytes);
        merged.Sections[0].Relocations.Count.ShouldBe(70000);
        merged.Sections[0].Relocations[40000].VirtualAddress.ShouldBe(40000u);
        merged.SymbolAt(merged.Sections[0].Relocations[69999].SymbolIndex).Name.ShouldBe("target");
    }

    [Test]
    public void Merge_rejects_objects_for_different_machines()
    {
        var first = new CoffBuilder();
        first.AddSection(".text", TextCharacteristics, new byte[4]);
        var second = new CoffBuilder();
        second.AddSection(".text", TextCharacteristics, new byte[4]);

        Should.Throw<InvalidOperationException>(() => CoffRelocatableObjects.Merge([first.Build(MachineAmd64), second.Build(MachineArm64)]))
            .Message.ShouldContain("machine");
    }

    // The auxiliary record of a weak external: the alternate's symbol index and the
    // IMAGE_WEAK_EXTERN_SEARCH_ALIAS characteristic.
    private static byte[] WeakExternalAux(int alternateIndex)
    {
        var aux = new byte[18];
        BinaryPrimitives.WriteUInt32LittleEndian(aux.AsSpan(0, 4), (uint)alternateIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(aux.AsSpan(4, 4), 3);
        return aux;
    }

    private static IrProgram LowerProgramWithImports(string source)
    {
        var parsed = ProjectSupport.ParseImportHeader(source, "<memory>");
        var layout = ProjectSupport.BuildStandaloneCompilationLayout(parsed.SourceWithoutImports, parsed.ImportNames);
        var importedStdModules = parsed.ImportNames.Where(ProjectSupport.IsStdModule).ToHashSet(StringComparer.Ordinal);

        var diagnostics = new Diagnostics();
        var program = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics, importedStdModules, parsed.ImportAliases.Count == 0 ? null : parsed.ImportAliases).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static async Task<(string Stdout, int ExitCode)> RunWindowsExecutableAsync(byte[] image)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ashes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string exePath = Path.Combine(directory, "split.exe");
        try
        {
            await File.WriteAllBytesAsync(exePath, image).ConfigureAwait(false);
            ProcessStartInfo psi = TestProcessHelper.CreateWindowsProcessStartInfo(exePath);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = directory;
            using Process process = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);
            string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            process.ExitCode.ShouldBe(0, $"stderr: {stderr}");
            return (stdout, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Builds a minimal COFF object: every section gets its section symbol with a
    /// section-definition auxiliary record, and symbol indices count auxiliary records.</summary>
    private sealed class CoffBuilder
    {
        private readonly List<BuilderSection> sections = [];
        private readonly List<BuilderSymbol> symbols = [];
        private readonly MemoryStream stringTable = new();
        private int recordCount;

        public CoffBuilder()
        {
            stringTable.Write(new byte[4]);
        }

        public int AddSection(string name, uint characteristics, byte[] data, byte comdatSelection = 0, bool uninitialized = false)
        {
            var section = new BuilderSection(name, characteristics, data, uninitialized);
            sections.Add(section);
            byte[] aux = new byte[18];
            BinaryPrimitives.WriteUInt32LittleEndian(aux.AsSpan(0, 4), (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(aux.AsSpan(12, 2), (ushort)sections.Count);
            aux[14] = comdatSelection;
            section.SymbolIndex = AddSymbol(name, 0, sections.Count, ClassStatic, aux);
            return sections.Count;
        }

        public int SectionSymbolIndex(int sectionNumber) => sections[sectionNumber - 1].SymbolIndex;

        public int AddSymbol(string name, uint value, int sectionNumber, byte storageClass, byte[]? aux = null)
        {
            int index = recordCount;
            symbols.Add(new BuilderSymbol(name, value, checked((short)sectionNumber), storageClass, aux ?? []));
            recordCount += 1 + ((aux?.Length ?? 0) / 18);
            return index;
        }

        public void AddRelocation(int sectionNumber, uint virtualAddress, int symbolIndex, ushort type) =>
            sections[sectionNumber - 1].Relocations.Add((virtualAddress, symbolIndex, type));

        public byte[] Build(ushort machine)
        {
            int cursor = 20 + (sections.Count * 40);
            foreach (BuilderSection section in sections)
            {
                section.PointerToRawData = section.Uninitialized ? 0 : cursor;
                cursor += section.Uninitialized ? 0 : section.Data.Length;
                section.PointerToRelocations = section.Relocations.Count == 0 ? 0 : cursor;
                cursor += section.Relocations.Count * 10;
            }

            int symbolTableOffset = cursor;
            var bytes = new byte[symbolTableOffset + (recordCount * 18)];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), machine);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), (ushort)sections.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)symbolTableOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), (uint)recordCount);
            for (int i = 0; i < sections.Count; i++)
            {
                WriteSection(bytes, 20 + (i * 40), sections[i]);
            }

            WriteSymbols(bytes.AsSpan(symbolTableOffset));
            byte[] strings = stringTable.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(strings.AsSpan(0, 4), (uint)strings.Length);
            return [.. bytes, .. strings];
        }

        private void WriteSection(byte[] bytes, int offset, BuilderSection section)
        {
            Span<byte> header = bytes.AsSpan(offset, 40);
            WriteName(header[..8], section.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), (uint)section.Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(20, 4), (uint)section.PointerToRawData);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), (uint)section.PointerToRelocations);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(32, 2), (ushort)section.Relocations.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(36, 4), section.Characteristics);
            if (!section.Uninitialized)
            {
                section.Data.CopyTo(bytes.AsSpan(section.PointerToRawData));
            }

            int relocationOffset = section.PointerToRelocations;
            foreach ((uint virtualAddress, int symbolIndex, ushort type) in section.Relocations)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(relocationOffset, 4), virtualAddress);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(relocationOffset + 4, 4), (uint)symbolIndex);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(relocationOffset + 8, 2), type);
                relocationOffset += 10;
            }
        }

        private void WriteSymbols(Span<byte> table)
        {
            int offset = 0;
            foreach (BuilderSymbol symbol in symbols)
            {
                Span<byte> record = table.Slice(offset, 18);
                WriteName(record[..8], symbol.Name);
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(8, 4), symbol.Value);
                BinaryPrimitives.WriteInt16LittleEndian(record.Slice(12, 2), symbol.SectionNumber);
                record[16] = symbol.StorageClass;
                record[17] = (byte)(symbol.Aux.Length / 18);
                symbol.Aux.CopyTo(table.Slice(offset + 18, symbol.Aux.Length));
                offset += 18 + symbol.Aux.Length;
            }
        }

        private void WriteName(Span<byte> field, string name)
        {
            byte[] encoded = Encoding.ASCII.GetBytes(name);
            if (encoded.Length <= 8)
            {
                encoded.CopyTo(field);
                return;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(field.Slice(4, 4), (uint)stringTable.Position);
            stringTable.Write(encoded);
            stringTable.WriteByte(0);
        }

        private sealed class BuilderSection(string name, uint characteristics, byte[] data, bool uninitialized)
        {
            public string Name { get; } = name;

            public uint Characteristics { get; } = characteristics;

            public byte[] Data { get; } = data;

            public bool Uninitialized { get; } = uninitialized;

            public List<(uint VirtualAddress, int SymbolIndex, ushort Type)> Relocations { get; } = [];

            public int SymbolIndex { get; set; }

            public int PointerToRawData { get; set; }

            public int PointerToRelocations { get; set; }
        }

        private sealed record BuilderSymbol(string Name, uint Value, short SectionNumber, byte StorageClass, byte[] Aux);
    }

    private sealed record ParsedRelocation(uint VirtualAddress, int SymbolIndex, ushort Type);

    private sealed record ParsedSection(string Name, uint Size, uint PointerToRawData, uint Characteristics, List<ParsedRelocation> Relocations);

    private sealed record ParsedSymbol(int RecordIndex, string Name, uint Value, short SectionNumber, byte StorageClass);

    /// <summary>Reads back a merged object the way the PE linker does: section headers (with the
    /// relocation overflow form), the symbol table by record index, and the string table.</summary>
    private sealed class ParsedCoff
    {
        private readonly Dictionary<int, ParsedSymbol> symbolsByRecord = [];

        public ushort Machine { get; private init; }

        public List<ParsedSection> Sections { get; } = [];

        public List<ParsedSymbol> Symbols { get; } = [];

        public ParsedSymbol SymbolAt(int recordIndex) => symbolsByRecord[recordIndex];

        public static ParsedCoff Parse(byte[] bytes)
        {
            var parsed = new ParsedCoff { Machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)) };
            int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2));
            int symbolTableOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
            int symbolCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
            int stringTableOffset = symbolTableOffset + (symbolCount * 18);
            for (int i = 0; i < sectionCount; i++)
            {
                parsed.Sections.Add(ParseSection(bytes, 20 + (i * 40), stringTableOffset));
            }

            for (int i = 0; i < symbolCount; i++)
            {
                ReadOnlySpan<byte> record = bytes.AsSpan(symbolTableOffset + (i * 18), 18);
                var symbol = new ParsedSymbol(
                    i,
                    ReadName(record[..8], bytes, stringTableOffset),
                    BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(8, 4)),
                    BinaryPrimitives.ReadInt16LittleEndian(record.Slice(12, 2)),
                    record[16]);
                parsed.Symbols.Add(symbol);
                parsed.symbolsByRecord[i] = symbol;
                i += record[17];
            }

            return parsed;
        }

        private static ParsedSection ParseSection(byte[] bytes, int offset, int stringTableOffset)
        {
            ReadOnlySpan<byte> header = bytes.AsSpan(offset, 40);
            uint characteristics = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(36, 4));
            int relocationOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24, 4));
            int relocationCount = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(32, 2));
            if ((characteristics & RelocationOverflowFlag) != 0 && relocationCount == 0xFFFF)
            {
                relocationCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(relocationOffset, 4)) - 1;
                relocationOffset += 10;
            }

            var relocations = new List<ParsedRelocation>(relocationCount);
            for (int i = 0; i < relocationCount; i++)
            {
                ReadOnlySpan<byte> record = bytes.AsSpan(relocationOffset + (i * 10), 10);
                relocations.Add(new ParsedRelocation(
                    BinaryPrimitives.ReadUInt32LittleEndian(record[..4]),
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(4, 4)),
                    BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(8, 2))));
            }

            return new ParsedSection(
                ReadName(header[..8], bytes, stringTableOffset),
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4)),
                characteristics,
                relocations);
        }

        private static string ReadName(ReadOnlySpan<byte> field, byte[] bytes, int stringTableOffset)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(field[..4]) == 0)
            {
                int start = stringTableOffset + (int)BinaryPrimitives.ReadUInt32LittleEndian(field.Slice(4, 4));
                int end = Array.IndexOf(bytes, (byte)0, start);
                return Encoding.ASCII.GetString(bytes, start, end - start);
            }

            int length = field.IndexOf((byte)0);
            return Encoding.ASCII.GetString(length < 0 ? field : field[..length]);
        }
    }
}
