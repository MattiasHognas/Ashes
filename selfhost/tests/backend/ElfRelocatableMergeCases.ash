// Pure cases for `AshesCompiler.Backend.ElfRelocatableMerge` on tiny hand-built relocatable
// objects: section concatenation at each input's alignment (PROGBITS and NOBITS), symbol and
// relocation rebasing, the global resolution rules, and the inputs the merge rejects. The
// objects are written by a fixture writer of their own here, not by the merge, so the merge's
// reader and writer are each checked against an independent layout.
import Ashes.Byte
import Ashes.Number.UInt
import Ashes.Text
import AshesCompiler.Backend.ElfRelocatableMerge
export (
    value runElfRelocatableMergeCases,
)

type FixtureSection =
    | fixtureName: Str
    | fixtureType: Int
    | fixtureFlags: Int
    | fixtureAlign: Int
    | fixtureInfo: Int
    | fixtureEntrySize: Int
    | fixtureSize: Int
    | fixtureContents: Bytes

type FixtureSymbol =
    | fixtureSymbolName: Str
    | fixtureSymbolInfo: Int
    | fixtureSymbolSection: Int
    | fixtureSymbolValue: Int

let fixtureX86Machine = 62

let fixtureArm64Machine = 183

let putFixtureWord offset value bytes =
    value
    |> Ashes.Number.UInt.fromInt64
    |> Ashes.Byte.setU64Le(bytes)(offset)

let recursive fixtureBytesFrom (values: List(Int)) (acc: Bytes) =
    match values with
        | [] -> acc
        | value :: rest ->
            value
            |> Ashes.Number.UInt.fromInt
            |> Ashes.Byte.appendByte(acc)
            |> fixtureBytesFrom(rest)

let fixtureBytes (values: List(Int)) =
    0
    |> Ashes.Byte.allocate
    |> fixtureBytesFrom(values)

let progBits (name: Str) (flags: Int) (align: Int) (contents: Bytes) =
    FixtureSection(
        fixtureName = name,
        fixtureType = 1,
        fixtureFlags = flags,
        fixtureAlign = align,
        fixtureInfo = 0,
        fixtureEntrySize = 0,
        fixtureSize = Ashes.Byte.length(contents),
        fixtureContents = contents
    )

let noBits (name: Str) (align: Int) (size: Int) =
    FixtureSection(
        fixtureName = name,
        fixtureType = 8,
        fixtureFlags = 3,
        fixtureAlign = align,
        fixtureInfo = 0,
        fixtureEntrySize = 0,
        fixtureSize = size,
        fixtureContents = Ashes.Byte.allocate(0)
    )

let recursive relaEntriesFrom (entries: List((Int, Int, Int, Int))) (acc: Bytes) =
    match entries with
        | [] -> acc
        | (offset, symbolIndex, relocationType, addend) :: rest ->
            (24
            |> Ashes.Byte.allocate
            |> putFixtureWord(0)(offset)
            |> putFixtureWord(8)(symbolIndex << 32 | relocationType)
            |> putFixtureWord(16)(addend)
            |> Ashes.Byte.append(acc)
            |> relaEntriesFrom(rest))

// A `.rela<target>` section for the section at `targetIndex`; its link is set to the symbol table
// by the writer.
let relaSection (name: Str) (targetIndex: Int) (entries: List((Int, Int, Int, Int))) =
    (let contents =
        0
        |> Ashes.Byte.allocate
        |> relaEntriesFrom(entries)
    in
        FixtureSection(
            fixtureName = name,
            fixtureType = 4,
            fixtureFlags = 64,
            fixtureAlign = 8,
            fixtureInfo = targetIndex,
            fixtureEntrySize = 24,
            fixtureSize = Ashes.Byte.length(contents),
            fixtureContents = contents
        ))

let fixtureSymbol (name: Str) (info: Int) (section: Int) (value: Int) = FixtureSymbol(fixtureSymbolName = name, fixtureSymbolInfo = info, fixtureSymbolSection = section, fixtureSymbolValue = value)

// `STB_LOCAL`/`STB_GLOBAL`/`STB_WEAK` with `STT_FUNC`, and a local `STT_SECTION`.
let localFunc = 2

let globalFunc = 18

let weakFunc = 34

let sectionSymbolInfo = 3

let recursive fixtureSymbolTables (symbols: List(FixtureSymbol)) (strtab: Bytes) (symtab: Bytes) =
    match symbols with
        | [] -> (strtab, symtab)
        | symbol :: rest ->
            (let nameOffset =
                if symbol.fixtureSymbolName == ""
                then 0
                else Ashes.Byte.length(strtab)
            in
                let nextStrtab =
                    if symbol.fixtureSymbolName == ""
                    then strtab
                    else
                        Ashes.Byte.appendByte(symbol.fixtureSymbolName
                        |> Ashes.Byte.fromText
                        |> Ashes.Byte.append(strtab))(0u8)
                in
                    24
                    |> Ashes.Byte.allocate
                    |> putFixtureWord(0)(nameOffset | symbol.fixtureSymbolInfo << 32 | symbol.fixtureSymbolSection << 48)
                    |> putFixtureWord(8)(symbol.fixtureSymbolValue)
                    |> Ashes.Byte.append(symtab)
                    |> fixtureSymbolTables(rest)(nextStrtab))

let recursive fixtureCount (sections: List(FixtureSection)) =
    match sections with
        | [] -> 0
        | _ :: rest -> 1 + fixtureCount(rest)

let recursive appendFixtureSections (left: List(FixtureSection)) (right: List(FixtureSection)) =
    match left with
        | [] -> right
        | section :: rest -> section :: appendFixtureSections(rest)(right)

let fixtureAlignUp value = (value + 7) / 8 * 8

// Writes every section's contents after the ELF header, each at the next 8-byte boundary, then
// the section header table; a section's name offset and file offset are assigned as it goes.
let recursive writeFixtureSections (sections: List(FixtureSection)) (symtabIndex: Int) (image: Bytes) (names: Bytes) (headers: Bytes) =
    match sections with
        | [] -> (image, names, headers)
        | section :: rest ->
            (let fileOffset =
                image
                |> Ashes.Byte.length
                |> fixtureAlignUp
            in
                let padded =
                    fileOffset - Ashes.Byte.length(image)
                    |> Ashes.Byte.allocate
                    |> Ashes.Byte.append(image)
                in
                    let link =
                        if section.fixtureType == 4
                        then symtabIndex
                        else
                            if section.fixtureType == 2
                            then symtabIndex + 1
                            else 0
                    in
                        let header =
                            64
                            |> Ashes.Byte.allocate
                            |> putFixtureWord(0)(Ashes.Byte.length(names) | section.fixtureType << 32)
                            |> putFixtureWord(8)(section.fixtureFlags)
                            |> putFixtureWord(24)(fileOffset)
                            |> putFixtureWord(32)(section.fixtureSize)
                            |> putFixtureWord(40)(link | section.fixtureInfo << 32)
                            |> putFixtureWord(48)(section.fixtureAlign)
                            |> putFixtureWord(56)(section.fixtureEntrySize)
                        in
                            header
                            |> Ashes.Byte.append(headers)
                            |> writeFixtureSections(rest)(symtabIndex)(Ashes.Byte.append(padded)(section.fixtureContents))(Ashes.Byte.appendByte(section.fixtureName
                            |> Ashes.Byte.fromText
                            |> Ashes.Byte.append(names))(0u8)))

// A relocatable object of `machine` with `sections` at indices 1.., then `.symtab` (the null
// symbol and `symbols`), `.strtab`, and `.shstrtab`.
let fixtureObject (machine: Int) (sections: List(FixtureSection)) (symbols: List(FixtureSymbol)) =
    (let symtabIndex = fixtureCount(sections) + 1
    in
        match 24
        |> Ashes.Byte.allocate
        |> fixtureSymbolTables(symbols)(Ashes.Byte.allocate(1)) with
            | (strtab, symtab) ->
                (let symtabSection = FixtureSection(fixtureName = ".symtab", fixtureType = 2, fixtureFlags = 0, fixtureAlign = 8, fixtureInfo = 1, fixtureEntrySize = 24, fixtureSize = Ashes.Byte.length(symtab), fixtureContents = symtab)
                in
                    let strtabSection = FixtureSection(fixtureName = ".strtab", fixtureType = 3, fixtureFlags = 0, fixtureAlign = 1, fixtureInfo = 0, fixtureEntrySize = 0, fixtureSize = Ashes.Byte.length(strtab), fixtureContents = strtab)
                    in
                        let leading = appendFixtureSections(sections)([symtabSection, strtabSection])
                        in
                            let namesWithoutSelf =
                                match 0
                                |> Ashes.Byte.allocate
                                |> writeFixtureSections(leading)(symtabIndex)(Ashes.Byte.allocate(64))(Ashes.Byte.allocate(1)) with
                                    | (_, names, _) -> names
                            in
                                let shstrtabContents =
                                    Ashes.Byte.appendByte(".shstrtab"
                                    |> Ashes.Byte.fromText
                                    |> Ashes.Byte.append(namesWithoutSelf))(0u8)
                                in
                                    let shstrtabSection = FixtureSection(fixtureName = ".shstrtab", fixtureType = 3, fixtureFlags = 0, fixtureAlign = 1, fixtureInfo = 0, fixtureEntrySize = 0, fixtureSize = Ashes.Byte.length(shstrtabContents), fixtureContents = shstrtabContents)
                                    in
                                        match 64
                                        |> Ashes.Byte.allocate
                                        |> writeFixtureSections(appendFixtureSections(leading)([shstrtabSection]))(symtabIndex)(Ashes.Byte.allocate(64))(Ashes.Byte.allocate(1)) with
                                            | (image, _, headers) ->
                                                (let shoff =
                                                    image
                                                    |> Ashes.Byte.length
                                                    |> fixtureAlignUp
                                                in
                                                    let sectionCount = symtabIndex + 3
                                                    in
                                                        let elf =
                                                            Ashes.Byte.append(shoff - Ashes.Byte.length(image)
                                                            |> Ashes.Byte.allocate
                                                            |> Ashes.Byte.append(image))(headers)
                                                        in
                                                            elf
                                                            |> putFixtureWord(0)(127 | 69 << 8 | 76 << 16 | 70 << 24 | 2 << 32 | 1 << 40 | 1 << 48)
                                                            |> putFixtureWord(16)(1 | machine << 16 | 1 << 32)
                                                            |> putFixtureWord(40)(shoff)
                                                            |> putFixtureWord(48)(64 << 32)
                                                            |> putFixtureWord(56)(64 << 16 | sectionCount << 32 | sectionCount - 1 << 48))))

let expectInt (label: Str) (expected: Int) (actual: Int) =
    if expected == actual
    then Unit
    else Ashes.IO.panic("ELF merge case: " + label + ": expected " + Ashes.Text.fromInt(expected) + ", got " + Ashes.Text.fromInt(actual))

let expectTrue (label: Str) (condition: Bool) =
    if condition
    then Unit
    else Ashes.IO.panic("ELF merge case: " + label)

let mergedOrPanic (label: Str) (objects: List(Bytes)) =
    match mergeElfRelocatableObjects(objects) with
        | Error(message) -> Ashes.IO.panic("ELF merge case: " + label + ": the merge failed: " + message)
        | Ok(merged) ->
            match parseElfMergeInput(merged) with
                | Error(message) -> Ashes.IO.panic("ELF merge case: " + label + ": the merged object does not parse: " + message)
                | Ok(input) -> input

let expectMergeError (label: Str) (needle: Str) (objects: List(Bytes)) =
    match mergeElfRelocatableObjects(objects) with
        | Ok(_) -> Ashes.IO.panic("ELF merge case: " + label + ": expected the merge to fail")
        | Error(message) ->
            if Ashes.Text.contains(message)(needle)
            then Unit
            else Ashes.IO.panic("ELF merge case: " + label + ": expected an error mentioning '" + needle + "', got: " + message)

let sectionOrPanic (input: ElfMergeInput) (name: Str) =
    match elfMergeSectionNamed(input)(name) with
        | None -> Ashes.IO.panic("ELF merge case: the merged object has no section " + name)
        | Some(found) -> found

let symbolOrPanic (input: ElfMergeInput) (name: Str) =
    match elfMergeSymbolNamed(input)(name) with
        | None -> Ashes.IO.panic("ELF merge case: the merged object has no symbol " + name)
        | Some(found) -> found

let sectionOfSymbol (input: ElfMergeInput) (index: Int) =
    (let symbol = elfMergeSymbolAt(input)(index)
    in symbol.inputSymbolSection)

let hasNoSection (input: ElfMergeInput) (name: Str) =
    match elfMergeSectionNamed(input)(name) with
        | None -> true
        | Some(_) -> false

let byteAt (bytes: Bytes) (offset: Int) =
    offset
    |> Ashes.Byte.get(bytes)
    |> Ashes.Number.UInt.toInt

let wordAt (bytes: Bytes) (offset: Int) =
    offset
    |> Ashes.Byte.getU64Le(bytes)
    |> Ashes.Number.UInt.toInt

// Two objects whose `.text` sections are 4- and 16-aligned and whose `.bss` sections are 8- and
// 16-aligned, the second with a relocation against its own section symbol and one against its
// own global.
let alignmentFirstObject unit =
    fixtureObject(fixtureX86Machine)([
        [1, 2, 3, 4, 5, 6]
        |> fixtureBytes
        |> progBits(".text")(6)(4),
        noBits(".bss")(8)(8)
    ])([
        fixtureSymbol("")(sectionSymbolInfo)(1)(0),
        fixtureSymbol("a_local")(localFunc)(1)(2)
    ])

let alignmentSecondObject unit =
    fixtureObject(fixtureX86Machine)([
        [7, 8, 9]
        |> fixtureBytes
        |> progBits(".text")(6)(16),
        relaSection(".rela.text")(1)([(1, 1, 2, 5), (0, 3, 4, -4)]),
        noBits(".bss")(16)(4)
    ])([
        fixtureSymbol("")(sectionSymbolInfo)(1)(0),
        fixtureSymbol("b_local")(localFunc)(1)(1),
        fixtureSymbol("b_global")(globalFunc)(1)(2)
    ])

let testElfMergeAlignsAndRebases unit =
    (let merged = mergedOrPanic("alignment")([alignmentFirstObject(Unit), alignmentSecondObject(Unit)])
    in
        match (sectionOrPanic(merged)(".text"), sectionOrPanic(merged)(".bss"), sectionOrPanic(merged)(".rela.text"), sectionOrPanic(merged)(".symtab")) with
            | ((textIndex, text), (bssIndex, bss), (_, rela), (_, symtab)) ->
                (let textBytes = elfMergeSectionBytes(merged)(text)
                in
                    match (symbolOrPanic(merged)("a_local"), symbolOrPanic(merged)("b_local"), symbolOrPanic(merged)("b_global")) with
                        | ((_, aLocal), (_, bLocal), (bGlobalIndex, bGlobal)) ->
                            (let relaBytes = elfMergeSectionBytes(merged)(rela)
                            in
                                Unit
                                |> (given (_) -> expectInt(".text is output section 1")(1)(textIndex))
                                |> (given (_) -> expectInt(".text size")(19)(text.mergeHeaderSize))
                                |> (given (_) -> expectInt(".text alignment")(16)(text.mergeHeaderAlign))
                                |> (given (_) ->
                                    5
                                    |> byteAt(textBytes)
                                    |> expectInt(".text byte 5")(6))
                                |> (given (_) ->
                                    6
                                    |> byteAt(textBytes)
                                    |> expectInt(".text padding byte 6")(0))
                                |> (given (_) ->
                                    15
                                    |> byteAt(textBytes)
                                    |> expectInt(".text padding byte 15")(0))
                                |> (given (_) ->
                                    16
                                    |> byteAt(textBytes)
                                    |> expectInt(".text byte 16")(7))
                                |> (given (_) ->
                                    18
                                    |> byteAt(textBytes)
                                    |> expectInt(".text byte 18")(9))
                                |> (given (_) -> expectInt(".bss type")(8)(bss.mergeHeaderType))
                                |> (given (_) -> expectInt(".bss size")(20)(bss.mergeHeaderSize))
                                |> (given (_) -> expectInt(".bss alignment")(16)(bss.mergeHeaderAlign))
                                |> (given (_) -> expectInt("a_local value")(2)(aLocal.inputSymbolValue))
                                |> (given (_) -> expectInt("a_local section")(textIndex)(aLocal.inputSymbolSection))
                                |> (given (_) -> expectInt("b_local value")(17)(bLocal.inputSymbolValue))
                                |> (given (_) -> expectInt("b_global value")(18)(bGlobal.inputSymbolValue))
                                |> (given (_) -> expectInt("b_global binding")(1)(bGlobal.inputSymbolInfo >> 4))
                                |> (given (_) -> expectInt("b_global index: section symbols, two locals, then the global")(5)(bGlobalIndex))
                                |> (given (_) -> expectInt(".symtab first global")(5)(symtab.mergeHeaderInfo))
                                |> (given (_) ->
                                    bssIndex
                                    |> sectionOfSymbol(merged)
                                    |> expectInt(".bss section symbol")(bssIndex))
                                |> (given (_) -> expectInt(".rela.text entries")(48)(rela.mergeHeaderSize))
                                |> (given (_) -> expectInt(".rela.text targets .text")(textIndex)(rela.mergeHeaderInfo))
                                |> (given (_) ->
                                    0
                                    |> wordAt(relaBytes)
                                    |> expectInt("section-symbol relocation offset")(17))
                                |> (given (_) ->
                                    8
                                    |> wordAt(relaBytes)
                                    |> expectInt("section-symbol relocation info")(1 << 32 | 2))
                                |> (given (_) ->
                                    16
                                    |> wordAt(relaBytes)
                                    |> expectInt("section-symbol relocation addend")(21))
                                |> (given (_) ->
                                    24
                                    |> wordAt(relaBytes)
                                    |> expectInt("global relocation offset")(16))
                                |> (given (_) ->
                                    32
                                    |> wordAt(relaBytes)
                                    |> expectInt("global relocation info")(bGlobalIndex << 32 | 4))
                                |> (given (_) ->
                                    40
                                    |> wordAt(relaBytes)
                                    |> expectInt("global relocation addend")(-4)))))

let textObjectWith (machine: Int) (contents: List(Int)) (symbols: List(FixtureSymbol)) =
    fixtureObject(machine)([contents
    |> fixtureBytes
    |> progBits(".text")(6)(4)])(symbols)

// An undefined reference to `shared` with a relocation against it, beside an object defining it.
let undefinedSharedObject unit =
    fixtureObject(fixtureX86Machine)([
        [1, 2, 3, 4]
        |> fixtureBytes
        |> progBits(".text")(6)(4),
        relaSection(".rela.text")(1)([(0, 1, 4, -4)])
    ])([fixtureSymbol("shared")(globalFunc)(0)(0)])

let definedSharedObject unit = textObjectWith(fixtureX86Machine)([5, 6])([fixtureSymbol("shared")(globalFunc)(1)(1)])

let testElfMergeDefinitionBeatsUndefined unit =
    (let undefinedFirst = mergedOrPanic("undefined first")([undefinedSharedObject(Unit), definedSharedObject(Unit)])
    in
        let definedFirst = mergedOrPanic("defined first")([definedSharedObject(Unit), undefinedSharedObject(Unit)])
        in
            match (symbolOrPanic(undefinedFirst)("shared"), symbolOrPanic(definedFirst)("shared"), sectionOrPanic(undefinedFirst)(".rela.text")) with
                | ((sharedIndex, afterUndefined), (_, afterDefined), (_, rela)) ->
                    (Unit
                    |> (given (_) -> expectInt("one global after the merge")(sharedIndex + 1)(undefinedFirst.mergeInputSymbolCount))
                    |> (given (_) -> expectInt("definition beats an earlier undefined reference: section")(1)(afterUndefined.inputSymbolSection))
                    |> (given (_) -> expectInt("definition beats an earlier undefined reference: value")(5)(afterUndefined.inputSymbolValue))
                    |> (given (_) -> expectInt("an undefined reference after the definition keeps it: section")(1)(afterDefined.inputSymbolSection))
                    |> (given (_) -> expectInt("an undefined reference after the definition keeps it: value")(1)(afterDefined.inputSymbolValue))
                    |> (given (_) ->
                        8
                        |> wordAt(elfMergeSectionBytes(undefinedFirst)(rela))
                        |> expectInt("relocation to the undefined name points at the resolved global")(sharedIndex << 32 | 4))))

let weakPickObject unit = textObjectWith(fixtureX86Machine)([1, 2, 3, 4])([fixtureSymbol("pick")(weakFunc)(1)(0)])

let globalPickObject unit = textObjectWith(fixtureX86Machine)([5, 6, 7, 8])([fixtureSymbol("pick")(globalFunc)(1)(1)])

let testElfMergeGlobalBeatsWeak unit =
    (let weakFirst = mergedOrPanic("weak first")([weakPickObject(Unit), globalPickObject(Unit)])
    in
        let globalFirst = mergedOrPanic("global first")([globalPickObject(Unit), weakPickObject(Unit)])
        in
            let twoWeak = mergedOrPanic("two weak")([weakPickObject(Unit), textObjectWith(fixtureX86Machine)([9])([fixtureSymbol("pick")(weakFunc)(1)(0)])])
            in
                match (symbolOrPanic(weakFirst)("pick"), symbolOrPanic(globalFirst)("pick"), symbolOrPanic(twoWeak)("pick")) with
                    | ((_, afterWeak), (_, afterGlobal), (_, afterTwoWeak)) ->
                        Unit
                        |> (given (_) -> expectInt("global beats an earlier weak: binding")(1)(afterWeak.inputSymbolInfo >> 4))
                        |> (given (_) -> expectInt("global beats an earlier weak: value")(5)(afterWeak.inputSymbolValue))
                        |> (given (_) -> expectInt("a later weak keeps the global: binding")(1)(afterGlobal.inputSymbolInfo >> 4))
                        |> (given (_) -> expectInt("a later weak keeps the global: value")(1)(afterGlobal.inputSymbolValue))
                        |> (given (_) -> expectInt("the first of two weak definitions is kept: binding")(2)(afterTwoWeak.inputSymbolInfo >> 4))
                        |> (given (_) -> expectInt("the first of two weak definitions is kept: value")(0)(afterTwoWeak.inputSymbolValue)))

let testElfMergeRejectsDuplicateGlobal unit = expectMergeError("duplicate global")("'pick' is defined in more than one")([globalPickObject(Unit), globalPickObject(Unit)])

let testElfMergeRejectsDebugSections unit =
    expectMergeError("debug section")(".debug_info")([
        weakPickObject(Unit),
        fixtureObject(fixtureX86Machine)([[1]
        |> fixtureBytes
        |> progBits(".debug_info")(0)(1)])([])
    ])

let testElfMergeRejectsMachineMismatch unit = expectMergeError("machine mismatch")("machines 62 and 183")([weakPickObject(Unit), textObjectWith(fixtureArm64Machine)([1])([])])

let testElfMergeRejectsDifferentFlags unit =
    expectMergeError("different flags")("different types or flags")([
        weakPickObject(Unit),
        fixtureObject(fixtureX86Machine)([[1]
        |> fixtureBytes
        |> progBits(".text")(2)(4)])([])
    ])

let testElfMergeRejectsRelSections unit =
    expectMergeError("SHT_REL")("without addends")([
        weakPickObject(Unit),
        fixtureObject(fixtureX86Machine)([
            [1]
            |> fixtureBytes
            |> progBits(".text")(6)(4),
            FixtureSection(fixtureName = ".rel.text", fixtureType = 9, fixtureFlags = 64, fixtureAlign = 8, fixtureInfo = 1, fixtureEntrySize = 16, fixtureSize = 0, fixtureContents = Ashes.Byte.allocate(0))
        ])([])
    ])

// `.llvm_addrsig` is dropped, a processor-specific section (`SHT_X86_64_UNWIND`, as LLVM types
// `.eh_frame`) is concatenated like PROGBITS, and an AArch64 pair merges like an x86-64 one.
let testElfMergeDropsAddrsigAndKeepsUnwind unit =
    (let addrsig = FixtureSection(fixtureName = ".llvm_addrsig", fixtureType = 1879002115, fixtureFlags = 2147483648, fixtureAlign = 1, fixtureInfo = 0, fixtureEntrySize = 0, fixtureSize = 1, fixtureContents = fixtureBytes([1]))
    in
        let unwind = FixtureSection(fixtureName = ".eh_frame", fixtureType = 1879048193, fixtureFlags = 2, fixtureAlign = 8, fixtureInfo = 0, fixtureEntrySize = 0, fixtureSize = 4, fixtureContents = fixtureBytes([1, 2, 3, 4]))
        in
            let merged =
                mergedOrPanic("addrsig")([
                    fixtureObject(fixtureArm64Machine)([[1]
                    |> fixtureBytes
                    |> progBits(".text")(6)(4), unwind, addrsig])([]),
                    fixtureObject(fixtureArm64Machine)([[2]
                    |> fixtureBytes
                    |> progBits(".text")(6)(4), unwind, addrsig])([])
                ])
            in
                match sectionOrPanic(merged)(".eh_frame") with
                    | (_, ehFrame) ->
                        Unit
                        |> (given (_) ->
                            ".llvm_addrsig"
                            |> hasNoSection(merged)
                            |> expectTrue(".llvm_addrsig is dropped"))
                        |> (given (_) -> expectInt(".eh_frame keeps its type")(1879048193)(ehFrame.mergeHeaderType))
                        |> (given (_) -> expectInt(".eh_frame is concatenated")(12)(ehFrame.mergeHeaderSize))
                        |> (given (_) -> expectInt("the machine is the inputs' machine")(fixtureArm64Machine)(merged.mergeInputMachine)))

let testElfMergeSingleObjectUnchanged unit =
    (let single = weakPickObject(Unit)
    in
        match mergeElfRelocatableObjects([single]) with
            | Error(message) -> Ashes.IO.panic("ELF merge case: a single object: " + message)
            | Ok(merged) ->
                merged
                |> Ashes.Byte.compare(single)
                |> expectInt("a single object is returned as it is")(0))

let runElfRelocatableMergeCases unit =
    unit
    |> testElfMergeSingleObjectUnchanged
    |> testElfMergeAlignsAndRebases
    |> testElfMergeDefinitionBeatsUndefined
    |> testElfMergeGlobalBeatsWeak
    |> testElfMergeRejectsDuplicateGlobal
    |> testElfMergeRejectsDebugSections
    |> testElfMergeRejectsMachineMismatch
    |> testElfMergeRejectsDifferentFlags
    |> testElfMergeRejectsRelSections
    |> testElfMergeDropsAddrsigAndKeepsUnwind
