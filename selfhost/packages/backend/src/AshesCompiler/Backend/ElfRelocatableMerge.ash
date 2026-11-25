// Merges several ELF64 relocatable objects into one, the way a relocatable link (`ld -r`) does, so
// the single-object linker `AshesCompiler.Backend.ElfLinker` can link a program whose code was
// emitted as several objects. Sections with the same name are concatenated at each input
// section's alignment, every symbol is rebased into the merged section it lands in, relocations
// keep their type and move with their section, and an undefined symbol one object references
// resolves to the definition another object provides. The relocation types are opaque to the
// merge, so one merge serves x86-64 and AArch64 alike.
//
// The steps follow stage 0's `ElfRelocatableObjects` (`Merge`, `PlaceSections`,
// `AddSectionSymbols`, `CollectLocalSymbols`, `CollectGlobalSymbols`, `CollectRelocations`,
// `Write`) one for one, with the same error messages. The merged object is built as a list of
// byte pieces joined once: an update to a `Bytes` that cannot prove its input unique copies the
// whole buffer, so writing fields one at a time into a large output would be quadratic. Every
// 16- and 32-bit field is packed into a 64-bit little-endian word, since there is no `Int` to
// `u32` conversion.
import Ashes.Byte
import Ashes.Number.UInt
import Ashes.Text
import Ashes.Collection.Map
import Ashes.Collection.List.reverse as reverseList
import AshesCompiler.Backend.PartitionDiagnostics.reportPartitionPhase
export (
    value mergeElfRelocatableObjects,
    value parseElfMergeInput,
    value elfMergeSectionNamed,
    value elfMergeSectionBytes,
    value elfMergeSymbolAt,
    value elfMergeSymbolNamed,
    value elfMergeSymbolName,
    type ElfMergeSectionHeader,
    type ElfMergeInput,
    type ElfMergeInputSymbol,
)

let sectionTypeProgBits = 1

let sectionTypeSymtab = 2

let sectionTypeStrtab = 3

let sectionTypeRela = 4

let sectionTypeNote = 7

let sectionTypeNoBits = 8

let sectionTypeRel = 9

// `SHT_LLVM_ADDRSIG` (`0x6fff4c03`).
let sectionTypeLlvmAddrsig = 1879002115

// `SHT_LOPROC` (`0x70000000`) and `SHT_HIPROC` (`0x7fffffff`).
let sectionTypeLowProcessor = 1879048192

let sectionTypeHighProcessor = 2147483647

let sectionFlagMerge = 16

let sectionFlagInfoLink = 64

// `SHN_LORESERVE` (`0xff00`) and `SHN_COMMON` (`0xfff2`).
let sectionIndexLowReserved = 65280

let sectionIndexCommon = 65522

let symbolBindingLocal = 0

let symbolBindingGlobal = 1

let symbolBindingWeak = 2

let symbolTypeSection = 3

let mergeElfHeaderSize = 64

let mergeSectionHeaderSize = 64

let mergeSymbolSize = 24

let mergeRelaSize = 24

let low32BitsMask = 4294967295

// A section header of an input object, with its name already read from the section name table.
type ElfMergeSectionHeader =
    | mergeHeaderName: Str
    | mergeHeaderType: Int
    | mergeHeaderFlags: Int
    | mergeHeaderOffset: Int
    | mergeHeaderSize: Int
    | mergeHeaderLink: Int
    | mergeHeaderInfo: Int
    | mergeHeaderAlign: Int
    | mergeHeaderEntrySize: Int

// One input object: its bytes, its section headers in index order, and where its symbol table and
// that table's string table lie. Symbols are read from the bytes on demand.
type ElfMergeInput =
    | mergeInputBytes: Bytes
    | mergeInputMachine: Int
    | mergeInputFlags: Int
    | mergeInputSections: List(ElfMergeSectionHeader)
    | mergeInputSymtabIndex: Int
    | mergeInputSymtabOffset: Int
    | mergeInputSymbolCount: Int
    | mergeInputStrtabOffset: Int
    | mergeInputStrtabSize: Int

type ElfMergeInputSymbol =
    | inputSymbolNameOffset: Int
    | inputSymbolInfo: Int
    | inputSymbolOther: Int
    | inputSymbolSection: Int
    | inputSymbolValue: Int
    | inputSymbolSize: Int

// A symbol of the merged object: its name, the section index it lands in, and its value rebased
// into that section.
type ElfMergeOutputSymbol =
    | outputSymbolName: Str
    | outputSymbolInfo: Int
    | outputSymbolOther: Int
    | outputSymbolSection: Int
    | outputSymbolValue: Int
    | outputSymbolSize: Int

// A section of the merged object: the concatenation of every input section of its name, with the
// relocations that target it. Both piece lists are kept newest first. Output section `i` is also
// symbol `i` of the merged symbol table, its local section symbol.
type ElfMergeOutputSection =
    | outputSectionIndex: Int
    | outputSectionName: Str
    | outputSectionType: Int
    | outputSectionFlags: Int
    | outputSectionEntrySize: Int
    | outputSectionAlignment: Int
    | outputSectionSize: Int
    | outputSectionPieces: List(Bytes)
    | outputSectionRelocations: List(Bytes)
    | outputSectionRelocationBytes: Int

type ElfMergeLayout =
    | layoutSections: MapTree(Int, ElfMergeOutputSection)
    | layoutSectionsByName: MapTree(Str, Int)
    | layoutSectionCount: Int

// What the merge learned about one input: which output section each of its merged sections went
// to and at what offset, which output symbol each of its symbols became, and for each of its
// section symbols the offset a relocation's addend grows by.
type ElfMergePlacedInput =
    | placedInput: ElfMergeInput
    | placedSectionTargets: MapTree(Int, (Int, Int))
    | placedSymbolIndices: MapTree(Int, Int)
    | placedSectionSymbolAddends: MapTree(Int, Int)

type ElfMergeGlobals =
    | globalSlots: MapTree(Str, Int)
    | globalSymbols: MapTree(Int, ElfMergeOutputSymbol)
    | globalCount: Int

// A section header of the merged object with its contents; a NOBITS section carries none.
type ElfMergeOutputHeader =
    | outputHeaderName: Str
    | outputHeaderType: Int
    | outputHeaderFlags: Int
    | outputHeaderLink: Int
    | outputHeaderInfo: Int
    | outputHeaderAlign: Int
    | outputHeaderEntrySize: Int
    | outputHeaderSize: Int
    | outputHeaderContents: Maybe(Bytes)

let readMergeU8 bytes offset =
    offset
    |> Ashes.Byte.get(bytes)
    |> Ashes.Number.UInt.toInt

let readMergeU16 bytes offset =
    offset
    |> Ashes.Byte.getU16Le(bytes)
    |> Ashes.Number.UInt.toInt

let readMergeU32 bytes offset =
    offset
    |> Ashes.Byte.getU32Le(bytes)
    |> Ashes.Number.UInt.toInt

let readMergeU64 bytes offset =
    offset
    |> Ashes.Byte.getU64Le(bytes)
    |> Ashes.Number.UInt.toInt

let putMergeWord offset value bytes =
    value
    |> Ashes.Number.UInt.fromInt64
    |> Ashes.Byte.setU64Le(bytes)(offset)

let alignMergeOffset value alignment =
    if alignment <= 1
    then value
    else (value + alignment - 1) / alignment * alignment

let largerOf left right =
    if left >= right
    then left
    else right

let recursive appendMergePairs (pieces: List(Bytes)) (acc: List(Bytes)) =
    match pieces with
        | first :: second :: rest -> appendMergePairs(rest)(Ashes.Byte.append(first)(second) :: acc)
        | single :: [] -> reverseList(single :: acc)
        | [] -> reverseList(acc)

// Joins the pieces pairwise, level by level, so every byte is copied once per level: n log n in
// the piece count where a left-to-right append chain would be quadratic.
let recursive joinMergePieces (pieces: List(Bytes)) =
    match pieces with
        | [] -> Ashes.Byte.allocate(0)
        | single :: [] -> single
        | _ ->
            []
            |> appendMergePairs(pieces)
            |> joinMergePieces

// A NUL-terminated string at `offset` inside the table that starts at `tableOffset`; a string
// without its NUL ends at the table's end.
let readMergeTableString (bytes: Bytes) (tableOffset: Int) (tableSize: Int) (offset: Int) =
    (let start = tableOffset + offset
    in
        let tableEnd = tableOffset + tableSize
        in
            let nulAt = Ashes.Byte.indexOf(bytes)(0)(start)
            in
                let stop =
                    if nulAt < 0 || nulAt > tableEnd
                    then tableEnd
                    else nulAt
                in
                    if stop <= start
                    then ""
                    else Ashes.Byte.subText(bytes)(start)(stop - start))

let readMergeSectionHeader (bytes: Bytes) (base_: Int) (namesOffset: Int) (namesSize: Int) =
    ElfMergeSectionHeader(
        mergeHeaderName = base_
        |> readMergeU32(bytes)
        |> readMergeTableString(bytes)(namesOffset)(namesSize),
        mergeHeaderType = readMergeU32(bytes)(base_ + 4),
        mergeHeaderFlags = readMergeU64(bytes)(base_ + 8),
        mergeHeaderOffset = readMergeU64(bytes)(base_ + 24),
        mergeHeaderSize = readMergeU64(bytes)(base_ + 32),
        mergeHeaderLink = readMergeU32(bytes)(base_ + 40),
        mergeHeaderInfo = readMergeU32(bytes)(base_ + 44),
        mergeHeaderAlign = readMergeU64(bytes)(base_ + 48),
        mergeHeaderEntrySize = readMergeU64(bytes)(base_ + 56)
    )

let recursive readMergeSectionHeaders bytes shoff shentsize count namesOffset namesSize index acc =
    if index >= count
    then reverseList(acc)
    else readMergeSectionHeaders(bytes)(shoff)(shentsize)(count)(namesOffset)(namesSize)(index + 1)(readMergeSectionHeader(bytes)(shoff + index * shentsize)(namesOffset)(namesSize) :: acc)

let recursive mergeHeaderAt (sections: List(ElfMergeSectionHeader)) (index: Int) =
    match sections with
        | [] -> None
        | header :: rest ->
            if index == 0
            then Some(header)
            else mergeHeaderAt(rest)(index - 1)

// The index of the last symbol table section, or -1 when there is none.
let recursive findMergeSymtabIndex (sections: List(ElfMergeSectionHeader)) (index: Int) (found: Int) =
    match sections with
        | [] -> found
        | header :: rest ->
            if header.mergeHeaderType == sectionTypeSymtab
            then findMergeSymtabIndex(rest)(index + 1)(index)
            else findMergeSymtabIndex(rest)(index + 1)(found)

let parseMergeSymbolTable (bytes: Bytes) (machine: Int) (flags: Int) (sections: List(ElfMergeSectionHeader)) =
    (let symtabIndex = findMergeSymtabIndex(sections)(0)(-1)
    in
        if symtabIndex < 0
        then Error("LLVM object did not contain a symbol table.")
        else
            match mergeHeaderAt(sections)(symtabIndex) with
                | None -> Error("LLVM object did not contain a symbol table.")
                | Some(symtab) ->
                    if symtab.mergeHeaderEntrySize != mergeSymbolSize
                    then Error("LLVM symbol table has an unexpected entry size.")
                    else
                        match mergeHeaderAt(sections)(symtab.mergeHeaderLink) with
                            | None -> Error("LLVM symbol table links to a missing string table.")
                            | Some(strtab) ->
                                let strtabSize =
                                    if strtab.mergeHeaderType == sectionTypeNoBits
                                    then 0
                                    else strtab.mergeHeaderSize
                                in
                                    Ok(ElfMergeInput(
                                        mergeInputBytes = bytes,
                                        mergeInputMachine = machine,
                                        mergeInputFlags = flags,
                                        mergeInputSections = sections,
                                        mergeInputSymtabIndex = symtabIndex,
                                        mergeInputSymtabOffset = symtab.mergeHeaderOffset,
                                        mergeInputSymbolCount = symtab.mergeHeaderSize / mergeSymbolSize,
                                        mergeInputStrtabOffset = strtab.mergeHeaderOffset,
                                        mergeInputStrtabSize = strtabSize
                                    )))

let parseMergeSections (bytes: Bytes) =
    (let shoff = readMergeU64(bytes)(40)
    in
        let shentsize = readMergeU16(bytes)(58)
        in
            let shnum = readMergeU16(bytes)(60)
            in
                if shentsize < mergeSectionHeaderSize || shnum == 0
                then Error("LLVM object is missing ELF section headers.")
                else
                    let namesBase = shoff + readMergeU16(bytes)(62) * shentsize
                    in
                        let namesOffset = readMergeU64(bytes)(namesBase + 24)
                        in
                            let namesSize = readMergeU64(bytes)(namesBase + 32)
                            in
                                []
                                |> readMergeSectionHeaders(bytes)(shoff)(shentsize)(shnum)(namesOffset)(namesSize)(0)
                                |> parseMergeSymbolTable(bytes)(readMergeU16(bytes)(18))(readMergeU32(bytes)(48)))

// Reads the header, section headers and symbol table location of one ELF64 little-endian
// relocatable object.
let parseElfMergeInput (bytes: Bytes) =
    if Ashes.Byte.length(bytes) < mergeElfHeaderSize || readMergeU8(bytes)(0) != 127 || readMergeU8(bytes)(1) != 69 || readMergeU8(bytes)(2) != 76 || readMergeU8(bytes)(3) != 70
    then Error("LLVM did not emit a valid ELF object.")
    else
        if readMergeU8(bytes)(4) != 2 || readMergeU8(bytes)(5) != 1
        then Error("Only ELF64 little-endian LLVM objects can be merged.")
        else
            if readMergeU16(bytes)(16) != 1
            then Error("Only relocatable ELF objects can be merged.")
            else parseMergeSections(bytes)

let elfMergeSymbolAt (input: ElfMergeInput) (index: Int) =
    (let base_ = input.mergeInputSymtabOffset + index * mergeSymbolSize
    in
        let bytes = input.mergeInputBytes
        in
            ElfMergeInputSymbol(
                inputSymbolNameOffset = readMergeU32(bytes)(base_),
                inputSymbolInfo = readMergeU8(bytes)(base_ + 4),
                inputSymbolOther = readMergeU8(bytes)(base_ + 5),
                inputSymbolSection = readMergeU16(bytes)(base_ + 6),
                inputSymbolValue = readMergeU64(bytes)(base_ + 8),
                inputSymbolSize = readMergeU64(bytes)(base_ + 16)
            ))

let elfMergeSymbolName (input: ElfMergeInput) (symbol: ElfMergeInputSymbol) = readMergeTableString(input.mergeInputBytes)(input.mergeInputStrtabOffset)(input.mergeInputStrtabSize)(symbol.inputSymbolNameOffset)

let recursive findMergeSectionNamed (sections: List(ElfMergeSectionHeader)) (name: Str) (index: Int) =
    match sections with
        | [] -> None
        | header :: rest ->
            if index > 0 && header.mergeHeaderName == name
            then Some((index, header))
            else findMergeSectionNamed(rest)(name)(index + 1)

// The first section of that name with its index.
let elfMergeSectionNamed (input: ElfMergeInput) (name: Str) = findMergeSectionNamed(input.mergeInputSections)(name)(0)

let recursive findMergeSymbolNamed (input: ElfMergeInput) (name: Str) (index: Int) =
    if index >= input.mergeInputSymbolCount
    then None
    else
        let symbol = elfMergeSymbolAt(input)(index)
        in
            if elfMergeSymbolName(input)(symbol) == name
            then Some((index, symbol))
            else findMergeSymbolNamed(input)(name)(index + 1)

// The first symbol of that name with its index; the null symbol is never matched.
let elfMergeSymbolNamed (input: ElfMergeInput) (name: Str) =
    if name == ""
    then None
    else findMergeSymbolNamed(input)(name)(1)

// A section's file image; a NOBITS section occupies no file bytes.
let elfMergeSectionBytes (input: ElfMergeInput) (header: ElfMergeSectionHeader) =
    if header.mergeHeaderType == sectionTypeNoBits || header.mergeHeaderSize == 0
    then Ashes.Byte.allocate(0)
    else
        Ashes.Byte.copyRange(Ashes.Byte.allocate(header.mergeHeaderSize))(0)(input.mergeInputBytes)(header.mergeHeaderOffset)(header.mergeHeaderSize)

let isMergeDebugSectionName (name: Str) =
    if Ashes.Text.startsWith(name)(".debug")
    then true
    else Ashes.Text.startsWith(name)(".rela.debug")

// Whether a section's contents are carried into the merged object. Symbol, string, and relocation
// tables are rebuilt; the LLVM address-significance table indexes the symbol table, so it is
// dropped; debug sections are not merged. Processor-specific section types (the x86-64 unwind type
// of .eh_frame) are concatenated like PROGBITS.
let isMergedSection (header: ElfMergeSectionHeader) =
    (let name = header.mergeHeaderName
    in
        let sectionType = header.mergeHeaderType
        in
            if isMergeDebugSectionName(name)
            then Error("ELF objects carrying the debug section '" + name + "' cannot be merged.")
            else
                if sectionType >= sectionTypeLowProcessor && sectionType <= sectionTypeHighProcessor
                then Ok(true)
                else
                    if sectionType == sectionTypeSymtab || sectionType == sectionTypeStrtab || sectionType == sectionTypeRela || sectionType == sectionTypeLlvmAddrsig
                    then Ok(false)
                    else
                        if sectionType == sectionTypeRel
                        then Error("ELF relocation section '" + name + "' without addends cannot be merged.")
                        else
                            if sectionType == sectionTypeProgBits || sectionType == sectionTypeNoBits || sectionType == sectionTypeNote
                            then Ok(true)
                            else Error("ELF section '" + name + "' of type " + Ashes.Text.fromInt(sectionType) + " cannot be merged."))

let newOutputSection (index: Int) (header: ElfMergeSectionHeader) =
    ElfMergeOutputSection(
        outputSectionIndex = index,
        outputSectionName = header.mergeHeaderName,
        outputSectionType = header.mergeHeaderType,
        outputSectionFlags = header.mergeHeaderFlags,
        outputSectionEntrySize = header.mergeHeaderEntrySize,
        outputSectionAlignment = 1,
        outputSectionSize = 0,
        outputSectionPieces = [],
        outputSectionRelocations = [],
        outputSectionRelocationBytes = 0
    )

let lookupOutputSection (layout: ElfMergeLayout) (index: Int) =
    match Ashes.Collection.Map.getInt(index)(layout.layoutSections) with
        | Some(section) -> section
        | None -> Ashes.IO.panic("ELF merge: output section " + Ashes.Text.fromInt(index) + " is missing from the layout")

let storeOutputSection (layout: ElfMergeLayout) (section: ElfMergeOutputSection) =
    ElfMergeLayout(
        layoutSections = Ashes.Collection.Map.setInt(section.outputSectionIndex)(section)(layout.layoutSections),
        layoutSectionsByName = layout.layoutSectionsByName,
        layoutSectionCount = layout.layoutSectionCount
    )

// The output section of this input section's name, created when it is the first of its name. A
// later section of the same name must agree on type and flags; one with a different entry size
// turns the merged section into an unmerged one of entry size 0.
let getOrAddOutputSection (layout: ElfMergeLayout) (header: ElfMergeSectionHeader) =
    (let name = header.mergeHeaderName
    in
        match Ashes.Collection.Map.getStr(name)(layout.layoutSectionsByName) with
            | None ->
                let index = layout.layoutSectionCount + 1
                in
                    Ok((ElfMergeLayout(
                        layoutSections = Ashes.Collection.Map.setInt(index)(newOutputSection(index)(header))(layout.layoutSections),
                        layoutSectionsByName = Ashes.Collection.Map.setStr(name)(index)(layout.layoutSectionsByName),
                        layoutSectionCount = index
                    ), newOutputSection(index)(header)))
            | Some(index) ->
                let section = lookupOutputSection(layout)(index)
                in
                    if section.outputSectionType != header.mergeHeaderType || section.outputSectionFlags != header.mergeHeaderFlags
                    then Error("ELF section '" + name + "' has different types or flags across the merged objects.")
                    else
                        if section.outputSectionEntrySize != header.mergeHeaderEntrySize
                        then Ok((layout, (section with outputSectionEntrySize = 0, outputSectionFlags = section.outputSectionFlags - (section.outputSectionFlags & sectionFlagMerge))))
                        else Ok((layout, section)))

// Appends one input section's contents at the next offset its alignment allows; returns the
// section and that offset. The padding piece is added even when it is empty, and a NOBITS
// section's pieces are never written.
let appendToOutputSection (section: ElfMergeOutputSection) (header: ElfMergeSectionHeader) (contents: Bytes) =
    (let alignment = largerOf(1)(header.mergeHeaderAlign)
    in
        let offset = alignMergeOffset(section.outputSectionSize)(alignment)
        in
            let pieces = contents :: Ashes.Byte.allocate(offset - section.outputSectionSize) :: section.outputSectionPieces
            in ((section with outputSectionAlignment = largerOf(section.outputSectionAlignment)(alignment), outputSectionSize = offset + header.mergeHeaderSize, outputSectionPieces = pieces), offset))

let recursive placeMergeSections (input: ElfMergeInput) (sections: List(ElfMergeSectionHeader)) (index: Int) (layout: ElfMergeLayout) (targets: MapTree(Int, (Int, Int))) =
    match sections with
        | [] -> Ok((layout, targets))
        | header :: rest ->
            match isMergedSection(header) with
                | Error(message) -> Error(message)
                | Ok(false) -> placeMergeSections(input)(rest)(index + 1)(layout)(targets)
                | Ok(true) ->
                    match getOrAddOutputSection(layout)(header) with
                        | Error(message) -> Error(message)
                        | Ok((withSection, section)) ->
                            match header
                            |> elfMergeSectionBytes(input)
                            |> appendToOutputSection(section)(header) with
                                | (appended, offset) ->
                                    targets
                                    |> Ashes.Collection.Map.setInt(index)((appended.outputSectionIndex, offset))
                                    |> placeMergeSections(input)(rest)(index + 1)(storeOutputSection(withSection)(appended))

// Places every merged section of one input; section 0 is the null section.
let placeInputSections (input: ElfMergeInput) (layout: ElfMergeLayout) =
    match input.mergeInputSections with
        | [] -> Ok((layout, Ashes.Collection.Map.empty))
        | _ :: rest -> placeMergeSections(input)(rest)(1)(layout)(Ashes.Collection.Map.empty)

let placedSymbolCount (placed: ElfMergePlacedInput) =
    match placed.placedInput with
        | ElfMergeInput { mergeInputSymbolCount = count } -> count

let placedBytes (placed: ElfMergePlacedInput) =
    match placed.placedInput with
        | ElfMergeInput { mergeInputBytes = bytes } -> bytes

let placedSymtabIndex (placed: ElfMergePlacedInput) =
    match placed.placedInput with
        | ElfMergeInput { mergeInputSymtabIndex = index } -> index

let placedSections (placed: ElfMergePlacedInput) =
    match placed.placedInput with
        | ElfMergeInput { mergeInputSections = sections } -> sections

let emptyMergeLayout = ElfMergeLayout(layoutSections = Ashes.Collection.Map.empty, layoutSectionsByName = Ashes.Collection.Map.empty, layoutSectionCount = 0)

let newPlacedInput (input: ElfMergeInput) (targets: MapTree(Int, (Int, Int))) =
    ElfMergePlacedInput(
        placedInput = input,
        placedSectionTargets = targets,
        placedSymbolIndices = Ashes.Collection.Map.setInt(0)(0)(Ashes.Collection.Map.empty),
        placedSectionSymbolAddends = Ashes.Collection.Map.empty
    )

// Parses every object and places its sections, in order; every object must be for the first
// one's machine.
let recursive parseAndPlaceInputs (objects: List(Bytes)) (machine: Int) (layout: ElfMergeLayout) (acc: List(ElfMergePlacedInput)) =
    match objects with
        | [] -> Ok((layout, reverseList(acc)))
        | bytes :: rest ->
            match parseElfMergeInput(bytes) with
                | Error(message) -> Error(message)
                | Ok(input) ->
                    let inputMachine = input.mergeInputMachine
                    in
                        let expectedMachine =
                            if machine < 0
                            then inputMachine
                            else machine
                        in
                            if inputMachine != expectedMachine
                            then Error("ELF objects for machines " + Ashes.Text.fromInt(expectedMachine) + " and " + Ashes.Text.fromInt(inputMachine) + " cannot be merged.")
                            else
                                match placeInputSections(input)(layout) with
                                    | Error(message) -> Error(message)
                                    | Ok((placed, targets)) -> parseAndPlaceInputs(rest)(expectedMachine)(placed)(newPlacedInput(input)(targets) :: acc)

// Every merged section gets one local section symbol, which the inputs' section symbols map to:
// symbol `i` names output section `i`. Returned newest first, as the locals accumulate.
let recursive sectionSymbols (index: Int) (count: Int) (acc: List(ElfMergeOutputSymbol)) =
    if index > count
    then acc
    else sectionSymbols(index + 1)(count)(ElfMergeOutputSymbol(outputSymbolName = "", outputSymbolInfo = symbolBindingLocal * 16 + symbolTypeSection, outputSymbolOther = 0, outputSymbolSection = index, outputSymbolValue = 0, outputSymbolSize = 0) :: acc)

// The symbol with its section index and value moved into the merged object, or None when its
// section was dropped.
let rebaseMergeSymbol (placed: ElfMergePlacedInput) (symbol: ElfMergeInputSymbol) =
    (let name = elfMergeSymbolName(placed.placedInput)(symbol)
    in
        let sectionIndex = symbol.inputSymbolSection
        in
            if sectionIndex == 0 || sectionIndex >= sectionIndexLowReserved
            then
                if sectionIndex == sectionIndexCommon
                then Error("ELF common symbol '" + name + "' cannot be merged.")
                else Ok(Some(ElfMergeOutputSymbol(outputSymbolName = name, outputSymbolInfo = symbol.inputSymbolInfo, outputSymbolOther = symbol.inputSymbolOther, outputSymbolSection = sectionIndex, outputSymbolValue = symbol.inputSymbolValue, outputSymbolSize = symbol.inputSymbolSize)))
            else
                match Ashes.Collection.Map.getInt(sectionIndex)(placed.placedSectionTargets) with
                    | None -> Ok(None)
                    | Some((outputIndex, offset)) -> Ok(Some(ElfMergeOutputSymbol(outputSymbolName = name, outputSymbolInfo = symbol.inputSymbolInfo, outputSymbolOther = symbol.inputSymbolOther, outputSymbolSection = outputIndex, outputSymbolValue = symbol.inputSymbolValue + offset, outputSymbolSize = symbol.inputSymbolSize))))

// One input's local symbols: a section symbol maps to its merged section's symbol with the input
// section's offset as the addend relocations add; any other local is rebased and appended.
let recursive collectInputLocals (placed: ElfMergePlacedInput) (index: Int) (locals: List(ElfMergeOutputSymbol)) (localCount: Int) =
    if index >= placedSymbolCount(placed)
    then Ok((placed, locals, localCount))
    else
        let symbol = elfMergeSymbolAt(placed.placedInput)(index)
        in
            if symbol.inputSymbolInfo >> 4 != symbolBindingLocal
            then collectInputLocals(placed)(index + 1)(locals)(localCount)
            else
                if symbol.inputSymbolInfo & 15 == symbolTypeSection
                then
                    match Ashes.Collection.Map.getInt(symbol.inputSymbolSection)(placed.placedSectionTargets) with
                        | None -> collectInputLocals(placed)(index + 1)(locals)(localCount)
                        | Some((outputIndex, offset)) -> collectInputLocals((placed with placedSymbolIndices = Ashes.Collection.Map.setInt(index)(outputIndex)(placed.placedSymbolIndices), placedSectionSymbolAddends = Ashes.Collection.Map.setInt(index)(offset)(placed.placedSectionSymbolAddends)))(index + 1)(locals)(localCount)
                else
                    match rebaseMergeSymbol(placed)(symbol) with
                        | Error(message) -> Error(message)
                        | Ok(None) -> collectInputLocals(placed)(index + 1)(locals)(localCount)
                        | Ok(Some(rebased)) -> collectInputLocals((placed with placedSymbolIndices = Ashes.Collection.Map.setInt(index)(localCount + 1)(placed.placedSymbolIndices)))(index + 1)(rebased :: locals)(localCount + 1)

let recursive collectAllLocals (inputs: List(ElfMergePlacedInput)) (locals: List(ElfMergeOutputSymbol)) (localCount: Int) (acc: List(ElfMergePlacedInput)) =
    match inputs with
        | [] -> Ok((reverseList(acc), locals, localCount))
        | placed :: rest ->
            match collectInputLocals(placed)(1)(locals)(localCount) with
                | Error(message) -> Error(message)
                | Ok((collected, nextLocals, nextCount)) -> collectAllLocals(rest)(nextLocals)(nextCount)(collected :: acc)

// A definition beats an undefined reference, a global definition beats a weak one, and two
// definitions of the same global name are an error.
let resolveMergeGlobal (existing: ElfMergeOutputSymbol) (candidate: ElfMergeOutputSymbol) =
    if candidate.outputSymbolSection == 0
    then Ok(existing)
    else
        if existing.outputSymbolSection == 0
        then Ok(candidate)
        else
            let existingBinding = existing.outputSymbolInfo >> 4
            in
                let candidateBinding = candidate.outputSymbolInfo >> 4
                in
                    if existingBinding == symbolBindingWeak && candidateBinding == symbolBindingGlobal
                    then Ok(candidate)
                    else
                        if candidateBinding == symbolBindingWeak
                        then Ok(existing)
                        else Error("ELF symbol '" + existing.outputSymbolName + "' is defined in more than one of the merged objects.")

// Stores the rebased global under its name's slot, resolving it against an earlier symbol of the
// same name; returns the globals and the slot.
let addMergeGlobal (globals: ElfMergeGlobals) (rebased: ElfMergeOutputSymbol) =
    match Ashes.Collection.Map.getStr(rebased.outputSymbolName)(globals.globalSlots) with
        | Some(slot) ->
            match Ashes.Collection.Map.getInt(slot)(globals.globalSymbols) with
                | None -> Ashes.IO.panic("ELF merge: global slot " + Ashes.Text.fromInt(slot) + " has no symbol")
                | Some(existing) ->
                    match resolveMergeGlobal(existing)(rebased) with
                        | Error(message) -> Error(message)
                        | Ok(resolved) -> Ok(((globals with globalSymbols = Ashes.Collection.Map.setInt(slot)(resolved)(globals.globalSymbols)), slot))
        | None ->
            let slot = globals.globalCount
            in
                Ok((ElfMergeGlobals(
                    globalSlots = Ashes.Collection.Map.setStr(rebased.outputSymbolName)(slot)(globals.globalSlots),
                    globalSymbols = Ashes.Collection.Map.setInt(slot)(rebased)(globals.globalSymbols),
                    globalCount = slot + 1
                ), slot))

// One input's global and weak symbols. Global symbol indices follow every object's locals; the
// locals are all collected first, so `localCount` is final here and each input symbol's output
// index is known at once.
let recursive collectInputGlobals (placed: ElfMergePlacedInput) (index: Int) (localCount: Int) (globals: ElfMergeGlobals) =
    if index >= placedSymbolCount(placed)
    then Ok((placed, globals))
    else
        let symbol = elfMergeSymbolAt(placed.placedInput)(index)
        in
            let binding = symbol.inputSymbolInfo >> 4
            in
                if binding == symbolBindingLocal
                then collectInputGlobals(placed)(index + 1)(localCount)(globals)
                else
                    if binding != symbolBindingGlobal && binding != symbolBindingWeak
                    then Error("ELF symbol '" + elfMergeSymbolName(placed.placedInput)(symbol) + "' has unsupported binding " + Ashes.Text.fromInt(binding) + ".")
                    else
                        match rebaseMergeSymbol(placed)(symbol) with
                            | Error(message) -> Error(message)
                            | Ok(None) -> Error("ELF global symbol '" + elfMergeSymbolName(placed.placedInput)(symbol) + "' lives in a section the merge drops.")
                            | Ok(Some(rebased)) ->
                                match addMergeGlobal(globals)(rebased) with
                                    | Error(message) -> Error(message)
                                    | Ok((nextGlobals, slot)) -> collectInputGlobals((placed with placedSymbolIndices = Ashes.Collection.Map.setInt(index)(1 + localCount + slot)(placed.placedSymbolIndices)))(index + 1)(localCount)(nextGlobals)

let recursive collectAllGlobals (inputs: List(ElfMergePlacedInput)) (localCount: Int) (globals: ElfMergeGlobals) (acc: List(ElfMergePlacedInput)) =
    match inputs with
        | [] -> Ok((reverseList(acc), globals))
        | placed :: rest ->
            match collectInputGlobals(placed)(1)(localCount)(globals) with
                | Error(message) -> Error(message)
                | Ok((collected, nextGlobals)) -> collectAllGlobals(rest)(localCount)(nextGlobals)(collected :: acc)

let relaEntryPiece (offset: Int) (info: Int) (addend: Int) =
    mergeRelaSize
    |> Ashes.Byte.allocate
    |> putMergeWord(0)(offset)
    |> putMergeWord(8)(info)
    |> putMergeWord(16)(addend)

// Each entry of one input relocation section with its offset rebased by the target section's
// offset, its symbol index remapped, and a section-symbol reference's addend grown by that section's
// offset. The relocation type, the low 32 bits of `r_info`, is kept as it is. The loop takes the
// object's bytes and the input's two symbol maps as plain parameters, read once per section: a
// helper returning a field of the input record hands back a copy of it, which per entry would copy
// the whole object.
let recursive rewriteRelocationEntries (bytes: Bytes) (symbolIndices: MapTree(Int, Int)) (sectionSymbolAddends: MapTree(Int, Int)) (targetName: Str) (entriesOffset: Int) (targetOffset: Int) (index: Int) (count: Int) (acc: List(Bytes)) =
    if index >= count
    then
        acc
        |> reverseList
        |> Ok
    else
        let entry = entriesOffset + index * mergeRelaSize
        in
            let info = readMergeU64(bytes)(entry + 8)
            in
                let symbolIndex = info >> 32
                in
                    match Ashes.Collection.Map.getInt(symbolIndex)(symbolIndices) with
                        | None -> Error("ELF relocation in '" + targetName + "' references symbol " + Ashes.Text.fromInt(symbolIndex) + ", which the merge dropped.")
                        | Some(mappedSymbol) ->
                            (let extraAddend =
                                match Ashes.Collection.Map.getInt(symbolIndex)(sectionSymbolAddends) with
                                    | Some(offset) -> offset
                                    | None -> 0
                            in rewriteRelocationEntries(bytes)(symbolIndices)(sectionSymbolAddends)(targetName)(entriesOffset)(targetOffset)(index + 1)(count)(relaEntryPiece(readMergeU64(bytes)(entry) + targetOffset)(mappedSymbol << 32 | info & low32BitsMask)(readMergeU64(bytes)(entry + 16) + extraAddend) :: acc))

let appendSectionRelocations (placed: ElfMergePlacedInput) (header: ElfMergeSectionHeader) (layout: ElfMergeLayout) =
    match placed with
        | ElfMergePlacedInput { placedInput = ElfMergeInput { mergeInputBytes = bytes }, placedSectionTargets = targets, placedSymbolIndices = symbolIndices, placedSectionSymbolAddends = sectionSymbolAddends } ->
            match Ashes.Collection.Map.getInt(header.mergeHeaderInfo)(targets) with
                | None -> Error("ELF relocation section '" + header.mergeHeaderName + "' targets a section the merge drops.")
                | Some((targetIndex, targetOffset)) ->
                    let target = lookupOutputSection(layout)(targetIndex)
                    in
                        let count = header.mergeHeaderSize / mergeRelaSize
                        in
                            match rewriteRelocationEntries(bytes)(symbolIndices)(sectionSymbolAddends)(target.outputSectionName)(header.mergeHeaderOffset)(targetOffset)(0)(count)([]) with
                                | Error(message) -> Error(message)
                                | Ok([]) -> Ok(layout)
                                | Ok(entries) ->
                                    (target with outputSectionRelocations = joinMergePieces(entries) :: target.outputSectionRelocations, outputSectionRelocationBytes = target.outputSectionRelocationBytes + count * mergeRelaSize)
                                    |> storeOutputSection(layout)
                                    |> Ok

let recursive collectInputRelocations (placed: ElfMergePlacedInput) (sections: List(ElfMergeSectionHeader)) (layout: ElfMergeLayout) =
    match sections with
        | [] -> Ok(layout)
        | header :: rest ->
            if header.mergeHeaderType != sectionTypeRela
            then collectInputRelocations(placed)(rest)(layout)
            else
                if header.mergeHeaderLink != placedSymtabIndex(placed) || header.mergeHeaderEntrySize != mergeRelaSize
                then Error("ELF relocation section '" + header.mergeHeaderName + "' has unexpected metadata.")
                else
                    match appendSectionRelocations(placed)(header)(layout) with
                        | Error(message) -> Error(message)
                        | Ok(next) -> collectInputRelocations(placed)(rest)(next)

let recursive collectAllRelocations (inputs: List(ElfMergePlacedInput)) (layout: ElfMergeLayout) =
    match inputs with
        | [] -> Ok(layout)
        | placed :: rest ->
            let sections =
                match placedSections(placed) with
                    | [] -> []
                    | _ :: afterNull -> afterNull
            in
                match collectInputRelocations(placed)(sections)(layout) with
                    | Error(message) -> Error(message)
                    | Ok(next) -> collectAllRelocations(rest)(next)

let recursive outputSectionsInOrder (layout: ElfMergeLayout) (index: Int) (acc: List(ElfMergeOutputSection)) =
    if index < 1
    then acc
    else outputSectionsInOrder(layout)(index - 1)(lookupOutputSection(layout)(index) :: acc)

let contentHeader (section: ElfMergeOutputSection) =
    ElfMergeOutputHeader(
        outputHeaderName = section.outputSectionName,
        outputHeaderType = section.outputSectionType,
        outputHeaderFlags = section.outputSectionFlags,
        outputHeaderLink = 0,
        outputHeaderInfo = 0,
        outputHeaderAlign = section.outputSectionAlignment,
        outputHeaderEntrySize = section.outputSectionEntrySize,
        outputHeaderSize = section.outputSectionSize,
        outputHeaderContents = if section.outputSectionType == sectionTypeNoBits
        then None
        else
            section.outputSectionPieces
            |> reverseList
            |> joinMergePieces
            |> Some
    )

let recursive contentHeaders (sections: List(ElfMergeOutputSection)) (acc: List(ElfMergeOutputHeader)) =
    match sections with
        | [] -> reverseList(acc)
        | section :: rest -> contentHeaders(rest)(contentHeader(section) :: acc)

// One `.rela<name>` section per merged section with relocations, in section order.
let recursive relocationHeaders (sections: List(ElfMergeOutputSection)) (symtabIndex: Int) (acc: List(ElfMergeOutputHeader)) =
    match sections with
        | [] -> reverseList(acc)
        | section :: rest ->
            if section.outputSectionRelocationBytes == 0
            then relocationHeaders(rest)(symtabIndex)(acc)
            else
                relocationHeaders(rest)(symtabIndex)(ElfMergeOutputHeader(
                    outputHeaderName = ".rela" + section.outputSectionName,
                    outputHeaderType = sectionTypeRela,
                    outputHeaderFlags = sectionFlagInfoLink,
                    outputHeaderLink = symtabIndex,
                    outputHeaderInfo = section.outputSectionIndex,
                    outputHeaderAlign = 8,
                    outputHeaderEntrySize = mergeRelaSize,
                    outputHeaderSize = section.outputSectionRelocationBytes,
                    outputHeaderContents = section.outputSectionRelocations
                    |> reverseList
                    |> joinMergePieces
                    |> Some
                ) :: acc)

let recursive countRelocatedSections (sections: List(ElfMergeOutputSection)) (count: Int) =
    match sections with
        | [] -> count
        | section :: rest ->
            if section.outputSectionRelocationBytes == 0
            then countRelocatedSections(rest)(count)
            else countRelocatedSections(rest)(count + 1)

let symbolEntryPiece (nameOffset: Int) (symbol: ElfMergeOutputSymbol) =
    mergeSymbolSize
    |> Ashes.Byte.allocate
    |> putMergeWord(0)(nameOffset | symbol.outputSymbolInfo << 32 | symbol.outputSymbolOther << 40 | symbol.outputSymbolSection << 48)
    |> putMergeWord(8)(symbol.outputSymbolValue)
    |> putMergeWord(16)(symbol.outputSymbolSize)

let nulTerminatedPiece (text: Str) =
    Ashes.Byte.appendByte(Ashes.Byte.fromText(text))(0u8)

// The string table (offset 0 the empty name) and symbol table entries of the symbols in order; a
// symbol without a name points at offset 0. Both piece lists come back newest first.
let recursive symbolTablePieces (symbols: List(ElfMergeOutputSymbol)) (strtabLength: Int) (strtabAcc: List(Bytes)) (symtabAcc: List(Bytes)) =
    match symbols with
        | [] -> (strtabAcc, symtabAcc)
        | symbol :: rest ->
            if symbol.outputSymbolName == ""
            then symbolTablePieces(rest)(strtabLength)(strtabAcc)(symbolEntryPiece(0)(symbol) :: symtabAcc)
            else symbolTablePieces(rest)(strtabLength + Ashes.Text.byteLength(symbol.outputSymbolName) + 1)(nulTerminatedPiece(symbol.outputSymbolName) :: strtabAcc)(symbolEntryPiece(strtabLength)(symbol) :: symtabAcc)

let recursive globalsInOrder (globals: ElfMergeGlobals) (slot: Int) (acc: List(ElfMergeOutputSymbol)) =
    if slot < 0
    then acc
    else
        match Ashes.Collection.Map.getInt(slot)(globals.globalSymbols) with
            | None -> Ashes.IO.panic("ELF merge: global slot " + Ashes.Text.fromInt(slot) + " has no symbol")
            | Some(symbol) -> globalsInOrder(globals)(slot - 1)(symbol :: acc)

// Locals first (`locals` holds them newest first), then the globals in slot order.
let recursive prependReversed (newestFirst: List(ElfMergeOutputSymbol)) (acc: List(ElfMergeOutputSymbol)) =
    match newestFirst with
        | [] -> acc
        | symbol :: rest -> prependReversed(rest)(symbol :: acc)

let tableHeader (name: Str) (sectionType: Int) (link: Int) (info: Int) (align: Int) (entrySize: Int) (contents: Bytes) =
    ElfMergeOutputHeader(
        outputHeaderName = name,
        outputHeaderType = sectionType,
        outputHeaderFlags = 0,
        outputHeaderLink = link,
        outputHeaderInfo = info,
        outputHeaderAlign = align,
        outputHeaderEntrySize = entrySize,
        outputHeaderSize = Ashes.Byte.length(contents),
        outputHeaderContents = Some(contents)
    )

// The section name table over every header's name, `.shstrtab` itself included, with each name's
// offset in header order.
let recursive sectionNamePieces (headers: List(ElfMergeOutputHeader)) (tableLength: Int) (pieces: List(Bytes)) (offsets: List(Int)) =
    match headers with
        | [] ->
            (pieces
            |> reverseList
            |> joinMergePieces, reverseList(offsets))
        | header :: rest -> sectionNamePieces(rest)(tableLength + Ashes.Text.byteLength(header.outputHeaderName) + 1)(nulTerminatedPiece(header.outputHeaderName) :: pieces)(tableLength :: offsets)

// Each header's file offset: sections laid out in order after the ELF header at their own
// alignment, a NOBITS section taking none of the file. Returns the offsets and the end position.
let recursive headerFileOffsets (headers: List(ElfMergeOutputHeader)) (position: Int) (acc: List(Int)) =
    match headers with
        | [] -> (reverseList(acc), position)
        | header :: rest ->
            let fileOffset = alignMergeOffset(position)(header.outputHeaderAlign)
            in
                match header.outputHeaderContents with
                    | None -> headerFileOffsets(rest)(fileOffset)(fileOffset :: acc)
                    | Some(_) -> headerFileOffsets(rest)(fileOffset + header.outputHeaderSize)(fileOffset :: acc)

let recursive contentPieces (headers: List(ElfMergeOutputHeader)) (fileOffsets: List(Int)) (cursor: Int) (acc: List(Bytes)) =
    match (headers, fileOffsets) with
        | (header :: rest, fileOffset :: restOffsets) ->
            match header.outputHeaderContents with
                | None -> contentPieces(rest)(restOffsets)(cursor)(acc)
                | Some(contents) -> contentPieces(rest)(restOffsets)(fileOffset + Ashes.Byte.length(contents))(contents :: Ashes.Byte.allocate(fileOffset - cursor) :: acc)
        | _ -> (acc, cursor)

let sectionHeaderPiece (header: ElfMergeOutputHeader) (nameOffset: Int) (fileOffset: Int) =
    mergeSectionHeaderSize
    |> Ashes.Byte.allocate
    |> putMergeWord(0)(nameOffset | header.outputHeaderType << 32)
    |> putMergeWord(8)(header.outputHeaderFlags)
    |> putMergeWord(24)(fileOffset)
    |> putMergeWord(32)(header.outputHeaderSize)
    |> putMergeWord(40)(header.outputHeaderLink | header.outputHeaderInfo << 32)
    |> putMergeWord(48)(header.outputHeaderAlign)
    |> putMergeWord(56)(header.outputHeaderEntrySize)

let recursive sectionHeaderPieces (headers: List(ElfMergeOutputHeader)) (nameOffsets: List(Int)) (fileOffsets: List(Int)) (acc: List(Bytes)) =
    match (headers, nameOffsets, fileOffsets) with
        | (header :: rest, nameOffset :: restNames, fileOffset :: restOffsets) -> sectionHeaderPieces(rest)(restNames)(restOffsets)(sectionHeaderPiece(header)(nameOffset)(fileOffset) :: acc)
        | _ -> acc

// The ELF header: the first input's identification bytes, machine and flags, no program headers,
// and the section header table at `sectionHeaderOffset`.
let mergedElfHeader (first: ElfMergeInput) (sectionHeaderOffset: Int) (sectionCount: Int) (sectionNamesIndex: Int) =
    16
    |> Ashes.Byte.copyRange(Ashes.Byte.allocate(mergeElfHeaderSize))(0)(first.mergeInputBytes)(0)
    |> putMergeWord(16)(1 | first.mergeInputMachine << 16 | 1 << 32)
    |> putMergeWord(40)(sectionHeaderOffset)
    |> putMergeWord(48)(first.mergeInputFlags | mergeElfHeaderSize << 32)
    |> putMergeWord(56)(mergeSectionHeaderSize << 16 | sectionCount << 32 | sectionNamesIndex << 48)

let recursive prependHeadersReversed (newestFirst: List(ElfMergeOutputHeader)) (acc: List(ElfMergeOutputHeader)) =
    match newestFirst with
        | [] -> acc
        | header :: rest -> prependHeadersReversed(rest)(header :: acc)

let appendHeaders (left: List(ElfMergeOutputHeader)) (right: List(ElfMergeOutputHeader)) =
    prependHeadersReversed(reverseList(left))(right)

let recursive headerCount (headers: List(ElfMergeOutputHeader)) (count: Int) =
    match headers with
        | [] -> count
        | _ :: rest -> headerCount(rest)(count + 1)

// `.symtab` (the null symbol, every local, then every global) and its `.strtab`.
let symbolTableHeaders (locals: List(ElfMergeOutputSymbol)) (localCount: Int) (globals: ElfMergeGlobals) (symtabIndex: Int) =
    (let symbols =
        []
        |> globalsInOrder(globals)(globals.globalCount - 1)
        |> prependReversed(locals)
    in
        match symbolTablePieces(symbols)(1)([Ashes.Byte.allocate(1)])([Ashes.Byte.allocate(mergeSymbolSize)]) with
            | (strtabPieces, symtabPieces) ->
                [
                    symtabPieces
                    |> reverseList
                    |> joinMergePieces
                    |> tableHeader(".symtab")(sectionTypeSymtab)(symtabIndex + 1)(1 + localCount)(8)(mergeSymbolSize),
                    strtabPieces
                    |> reverseList
                    |> joinMergePieces
                    |> tableHeader(".strtab")(sectionTypeStrtab)(0)(0)(1)(0)
                ])

// The file: ELF header, every section's contents at its file offset, then the section header
// table (the null header first) at the next 8-byte boundary.
let assembleMergedObject (first: ElfMergeInput) (headers: List(ElfMergeOutputHeader)) (nameOffsets: List(Int)) =
    match headerFileOffsets(headers)(mergeElfHeaderSize)([]) with
        | (fileOffsets, endPosition) ->
            let sectionHeaderOffset = alignMergeOffset(endPosition)(8)
            in
                let sectionNamesIndex = headerCount(headers)(0)
                in
                    match contentPieces(headers)(fileOffsets)(mergeElfHeaderSize)([mergedElfHeader(first)(sectionHeaderOffset)(sectionNamesIndex + 1)(sectionNamesIndex)]) with
                        | (pieces, cursor) ->
                            Ashes.Byte.allocate(mergeSectionHeaderSize) :: Ashes.Byte.allocate(sectionHeaderOffset - cursor) :: pieces
                            |> sectionHeaderPieces(headers)(nameOffsets)(fileOffsets)
                            |> reverseList
                            |> joinMergePieces

// Lays the merged object out as the merged sections, one .rela<name> section per merged section
// with relocations, then .symtab, .strtab, and .shstrtab, followed by the section header table.
let writeMergedObject (first: ElfMergeInput) (layout: ElfMergeLayout) (locals: List(ElfMergeOutputSymbol)) (localCount: Int) (globals: ElfMergeGlobals) =
    (let sections = outputSectionsInOrder(layout)(layout.layoutSectionCount)([])
    in
        let symtabIndex = 1 + layout.layoutSectionCount + countRelocatedSections(sections)(0)
        in
            let withoutNames =
                symtabIndex
                |> symbolTableHeaders(locals)(localCount)(globals)
                |> appendHeaders(relocationHeaders(sections)(symtabIndex)([]))
                |> appendHeaders(contentHeaders(sections)([]))
            in
                let nameTableHeader =
                    0
                    |> Ashes.Byte.allocate
                    |> tableHeader(".shstrtab")(sectionTypeStrtab)(0)(0)(1)(0)
                in
                    match sectionNamePieces(appendHeaders(withoutNames)([nameTableHeader]))(1)([Ashes.Byte.allocate(1)])([]) with
                        | (nameTable, nameOffsets) ->
                            assembleMergedObject(first)(appendHeaders(withoutNames)([tableHeader(".shstrtab")(sectionTypeStrtab)(0)(0)(1)(0)(nameTable)]))(nameOffsets))

// Merges ELF64 relocatable objects of one machine into one relocatable object, or reports why
// they cannot be merged. A single object is returned as it is.
let mergeElfRelocatableObjects (objects: List(Bytes)) =
    match objects with
        | [] -> Error("No ELF objects were given to merge.")
        | single :: [] -> Ok(single)
        | _ ->
            match parseAndPlaceInputs(objects)(-1)(emptyMergeLayout)([]) with
                | Error(message) -> Error(message)
                | Ok((layout, placedInputs)) ->
                    let _ = reportPartitionPhase("partition merge parsed and placed")
                    in
                        let sectionCount = layout.layoutSectionCount
                        in
                            match collectAllLocals(placedInputs)(sectionSymbols(1)(sectionCount)([]))(sectionCount)([]) with
                                | Error(message) -> Error(message)
                                | Ok((withLocals, locals, localCount)) ->
                                    let _ = reportPartitionPhase("partition merge locals")
                                    in
                                        match collectAllGlobals(withLocals)(localCount)(ElfMergeGlobals(globalSlots = Ashes.Collection.Map.empty, globalSymbols = Ashes.Collection.Map.empty, globalCount = 0))([]) with
                                            | Error(message) -> Error(message)
                                            | Ok((withGlobals, globals)) ->
                                                let _ = reportPartitionPhase("partition merge globals")
                                                in
                                                    match collectAllRelocations(withGlobals)(layout) with
                                                        | Error(message) -> Error(message)
                                                        | Ok(relocated) ->
                                                            let _ = reportPartitionPhase("partition merge relocations")
                                                            in
                                                                match withGlobals with
                                                                    | [] -> Error("No ELF objects were given to merge.")
                                                                    | firstPlaced :: _ ->
                                                                        let written = writeMergedObject(firstPlaced.placedInput)(relocated)(locals)(localCount)(globals)
                                                                        in
                                                                            let _ = reportPartitionPhase("partition merge written")
                                                                            in Ok(written)
