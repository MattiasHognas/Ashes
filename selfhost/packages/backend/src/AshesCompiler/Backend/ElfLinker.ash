// A minimal, STATIC-ONLY ELF64 executable linker for linux-x64: turns the relocatable object
// `AshesCompiler.Backend.Llvm`'s own `targetMachineEmitToMemoryBuffer(...)(objectFileType)` emits
// into a directly-runnable executable's bytes, entirely in pure Ashes byte manipulation
// (`Ashes.Byte`) — no LLVM API calls, no external linker (`ld`/`lld`) invoked.
//
// Deliberately narrow first slice, matching this arc's target-narrowing precedent: covers exactly
// what `AshesCompiler.Backend.IrCodegen` can produce today, a SINGLE self-contained function with
// no external symbol references and no data relocations (arithmetic/locals/control-flow/entry-
// exit-syscall; no malloc/libc calls, no closures, no RC yet). The real linker
// (`LlvmImageLinkerElf.cs`) inserts an argv-passing trampoline before the object's own code and
// supports dynamic linking (PLT/GOT, imported libraries, the `.dynamic` section), TLS sections,
// and relocation application; none of that exists here. `linkStaticLinuxExecutable` refuses
// (`Error`, never a silent mislink) any object whose `.text` carries relocations — the next slice,
// once `IrCodegen` needs to call another function or reference global data.
//
// ELF field offsets and values below are taken directly from `LlvmImageLinkerElf.cs`'s own
// `WriteElf64Header`/`WriteElf64ProgramHeader`/`ParseElfObject`, not invented independently: the
// same base virtual address (`0x400000`, one page below where `.text` is placed), and the same
// symbol-table-driven entry-offset lookup (an entry function need not start at byte 0 of `.text`,
// though every module `IrCodegen` builds today is exactly one function, so it always does).

import Ashes.Byte
import Ashes.Number.UInt
export (
    value linkStaticLinuxExecutable,
)

type ElfSectionHeader =
    | sectionNameOffset: Int
    | sectionType: Int
    | sectionOffset: Int
    | sectionSize: Int
    | sectionLink: Int
    | sectionInfo: Int

type ElfSymbol =
    | symNameOffset: Int
    | symType: Int
    | symSectionIndex: Int
    | symValue: Int

let elfHeaderSize = 64

let elfProgramHeaderSize = 56

let pageSize = 4096

// `0x400000`: the same fixed non-PIE base virtual address `LlvmImageLinkerElf.cs` uses.
let elfBaseVa = 4194304

let putU8 offset value bytes = Ashes.Byte.set(bytes)(offset)(value)

let putU16 offset value bytes = Ashes.Byte.setU16Le(bytes)(offset)(value)

let putU32 offset value bytes = Ashes.Byte.setU32Le(bytes)(offset)(value)

let putU64 offset value bytes = Ashes.Byte.setU64Le(bytes)(offset)(value)

let getU32 bytes offset =
    offset
    |> Ashes.Byte.getU32Le(bytes)
    |> Ashes.Number.UInt.toInt

let getU64 bytes offset =
    offset
    |> Ashes.Byte.getU64Le(bytes)
    |> Ashes.Number.UInt.toInt

let getU16 bytes offset =
    offset
    |> Ashes.Byte.getU16Le(bytes)
    |> Ashes.Number.UInt.toInt

let getU8 bytes offset =
    offset
    |> Ashes.Byte.get(bytes)
    |> Ashes.Number.UInt.toInt

// Reads a NUL-terminated string starting at `offset` in `bytes` — every ELF name (section names in
// `.shstrtab`, symbol names in the string table a `.symtab`'s `sh_link` names) is stored this way.
let readElfString bytes offset =
    (let nulAt = Ashes.Byte.indexOf(bytes)(0)(offset)
    in
        if nulAt < 0
        then Ashes.Byte.subText(bytes)(offset)(Ashes.Byte.length(bytes) - offset)
        else Ashes.Byte.subText(bytes)(offset)(nulAt - offset))

let readSectionHeader bytes shoff shentsize index =
    (let base_ = shoff + index * shentsize
    in
        ElfSectionHeader(
            sectionNameOffset = getU32(bytes)(base_),
            sectionType = getU32(bytes)(base_ + 4),
            sectionOffset = getU64(bytes)(base_ + 24),
            sectionSize = getU64(bytes)(base_ + 32),
            sectionLink = getU32(bytes)(base_ + 40),
            sectionInfo = getU32(bytes)(base_ + 44)
        ))

let readSymbol bytes symtabOffset index =
    (let base_ = symtabOffset + index * 24
    in
        ElfSymbol(
            symNameOffset = getU32(bytes)(base_),
            symType = getU8(bytes)(base_ + 4) & 15,
            symSectionIndex = getU16(bytes)(base_ + 6),
            symValue = getU64(bytes)(base_ + 8)
        ))

let recursive findSectionIndexByName bytes shoff shentsize shnum shstrtabOffset name index =
    if index >= shnum
    then None
    else
        let section = readSectionHeader(bytes)(shoff)(shentsize)(index)
        in
            let sectionName = readElfString(bytes)(shstrtabOffset + section.sectionNameOffset)
            in
                if sectionName == name
                then Some((index, section))
                else findSectionIndexByName(bytes)(shoff)(shentsize)(shnum)(shstrtabOffset)(name)(index + 1)

// `STT_FUNC` (`symType == 2`) only: an object's symbol table also carries a `FILE`-typed symbol
// naming the compilation unit, which — for a test module whose LLVM module ID happens to equal
// its entry function's own name (as every hand-built module in `selfhost/tests/backend` does) —
// collides with the entry symbol's own name. Matching by name alone would find that `FILE` symbol
// first (it has no real section, so it can never satisfy the caller's `.text` check) instead of
// the actual function.
let recursive findSymbolByName bytes symtabOffset symbolCount strtabOffset name index =
    if index >= symbolCount
    then None
    else
        let symbol = readSymbol(bytes)(symtabOffset)(index)
        in
            if symbol.symType != 2
            then findSymbolByName(bytes)(symtabOffset)(symbolCount)(strtabOffset)(name)(index + 1)
            else
                let symbolName = readElfString(bytes)(strtabOffset + symbol.symNameOffset)
                in
                    if symbolName == name
                    then Some(symbol)
                    else findSymbolByName(bytes)(symtabOffset)(symbolCount)(strtabOffset)(name)(index + 1)

// True if any `SHT_RELA`(4)/`SHT_REL`(9) section targets `.text` (via `sh_info`) with at least one
// entry. A relocation here means the function calls something outside itself or reads global
// data — both out of scope for this slice; the caller turns this into an `Error` rather than
// linking a binary with unresolved/ignored relocations.
let recursive hasTextRelocations bytes shoff shentsize shnum textSectionIndex index =
    if index >= shnum
    then false
    else
        let section = readSectionHeader(bytes)(shoff)(shentsize)(index)
        in
            if section.sectionSize == 0
            then hasTextRelocations(bytes)(shoff)(shentsize)(shnum)(textSectionIndex)(index + 1)
            else
                if section.sectionInfo != textSectionIndex
                then hasTextRelocations(bytes)(shoff)(shentsize)(shnum)(textSectionIndex)(index + 1)
                else
                    match section.sectionType with
                        | 4 -> true
                        | 9 -> true
                        | _ -> hasTextRelocations(bytes)(shoff)(shentsize)(shnum)(textSectionIndex)(index + 1)

// `e_ident`: magic (`\x7fELF`), 64-bit class, little-endian data, current version, no OS/ABI —
// bytes 8..15 stay zero (already true of a freshly `allocate`d buffer).
let writeElfIdent bytes =
    bytes
    |> putU8(0)(127u8)
    |> putU8(1)(69u8)
    |> putU8(2)(76u8)
    |> putU8(3)(70u8)
    |> putU8(4)(2u8)
    |> putU8(5)(1u8)
    |> putU8(6)(1u8)
    |> putU8(7)(0u8)

let writeElfHeaderFields entryPoint bytes =
    bytes
    |> putU16(16)(2u16)
    |> putU16(18)(62u16)
    |> putU32(20)(1u32)
    |> putU64(24)(entryPoint)
    |> putU64(32)(Ashes.Number.UInt.fromInt64(elfHeaderSize))
    |> putU64(40)(0u64)
    |> putU32(48)(0u32)
    |> putU16(52)(64u16)
    |> putU16(54)(56u16)
    |> putU16(56)(1u16)
    |> putU16(58)(0u16)
    |> putU16(60)(0u16)
    |> putU16(62)(0u16)

// One `PT_LOAD` segment (`p_type = 1`) covering the whole file, `R+X` (`p_flags = 5`), mapped
// starting at `elfBaseVa` — no separate read-only header region and executable `.text` region,
// since this slice never emits writable data.
let writeProgramHeader totalLoadSize bytes =
    (let base_ = elfHeaderSize
    in
        bytes
        |> putU32(base_ + 0)(1u32)
        |> putU32(base_ + 4)(5u32)
        |> putU64(base_ + 8)(0u64)
        |> putU64(base_ + 16)(Ashes.Number.UInt.fromInt64(elfBaseVa))
        |> putU64(base_ + 24)(Ashes.Number.UInt.fromInt64(elfBaseVa))
        |> putU64(base_ + 32)(Ashes.Number.UInt.fromInt64(totalLoadSize))
        |> putU64(base_ + 40)(Ashes.Number.UInt.fromInt64(totalLoadSize))
        |> putU64(base_ + 48)(Ashes.Number.UInt.fromInt64(pageSize)))

let buildHeaderPage entryPoint totalLoadSize =
    pageSize
    |> Ashes.Byte.allocate
    |> writeElfIdent
    |> writeElfHeaderFields(Ashes.Number.UInt.fromInt64(entryPoint))
    |> writeProgramHeader(totalLoadSize)

// Links a single-function, relocation-free LLVM object (produced by
// `targetMachineEmitToMemoryBuffer(...)(objectFileType)`) into a runnable static ELF64 executable.
// `entrySymbolName` must name a function defined in the object's `.text` section — the file's
// `e_entry` is set to that symbol's own address, not merely the start of `.text`, so a future
// multi-function object still links correctly as long as `.text` carries no relocations.
//
// Layout: `.text` is placed verbatim starting at file offset `pageSize` (VA `elfBaseVa +
// pageSize`), with the ELF + program header occupying the low part of that same first page (the
// rest zero-padded, harmless since it is never read). No section headers are written — the kernel
// loader only consults program headers to run a binary.
let linkStaticLinuxExecutable objectBytes entrySymbolName =
    (let shoff = getU64(objectBytes)(40)
    in
        let shentsize = getU16(objectBytes)(58)
        in
            let shnum = getU16(objectBytes)(60)
            in
                let shstrndx = getU16(objectBytes)(62)
                in
                    let shstrtabHeader = readSectionHeader(objectBytes)(shoff)(shentsize)(shstrndx)
                    in
                        let shstrtabOffset = shstrtabHeader.sectionOffset
                        in
                            match findSectionIndexByName(objectBytes)(shoff)(shentsize)(shnum)(shstrtabOffset)(".text")(0) with
                                | None -> Error("static linker: object has no .text section")
                                | Some((textSectionIndex, textSection)) ->
                                    match findSectionIndexByName(objectBytes)(shoff)(shentsize)(shnum)(shstrtabOffset)(".symtab")(0) with
                                        | None -> Error("static linker: object has no symbol table")
                                        | Some((_, symtabSection)) ->
                                            if hasTextRelocations(objectBytes)(shoff)(shentsize)(shnum)(textSectionIndex)(0)
                                            then
                                                Error(
                                                    "static linker: .text has relocations (external calls or global data); dynamic linking is not supported yet"
                                                )
                                            else
                                                let strtabSection = readSectionHeader(objectBytes)(shoff)(shentsize)(symtabSection.sectionLink)
                                                in
                                                    let symbolCount = symtabSection.sectionSize / 24
                                                    in
                                                        match findSymbolByName(objectBytes)(symtabSection.sectionOffset)(symbolCount)(strtabSection.sectionOffset)(
                                                            entrySymbolName
                                                        )(0) with
                                                            | None -> Error("static linker: object does not define entry symbol '" + entrySymbolName + "'")
                                                            | Some(entrySymbol) ->
                                                                if entrySymbol.symSectionIndex != textSectionIndex
                                                                then Error("static linker: entry symbol '" + entrySymbolName + "' is not defined in .text")
                                                                else
                                                                    let textBytes =
                                                                        Ashes.Byte.copyRange(Ashes.Byte.allocate(textSection.sectionSize))(0)(objectBytes)(
                                                                            textSection.sectionOffset
                                                                        )(textSection.sectionSize)
                                                                    in
                                                                        let textVa = elfBaseVa + pageSize
                                                                        in
                                                                            let entryPoint = textVa + entrySymbol.symValue
                                                                            in
                                                                                let totalLoadSize = pageSize + textSection.sectionSize
                                                                                in
                                                                                    textBytes
                                                                                    |> Ashes.Byte.append(buildHeaderPage(entryPoint)(totalLoadSize))
                                                                                    |> Ok)
