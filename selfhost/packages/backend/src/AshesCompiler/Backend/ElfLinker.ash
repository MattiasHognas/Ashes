// An ELF64 executable linker for linux-x64: turns the relocatable object
// `AshesCompiler.Backend.Llvm`'s own `targetMachineEmitToMemoryBuffer(...)(objectFileType)` emits
// into a directly-runnable executable's bytes, entirely in pure Ashes byte manipulation
// (`Ashes.Byte`) — no LLVM API calls, no external linker (`ld`/`lld`) invoked.
//
// Two paths, chosen automatically from what the object's `.text` relocations need. Either can
// carry a `.rodata` segment (a string literal's own static storage), patched via an absolute
// (`R_X86_64_32`/`R_X86_64_32S`, `S + A`) or PC-relative (`R_X86_64_PC32`, `S + A - P`) reference
// depending on what LLVM's instruction selection chose for that particular object — both shapes
// are observed in practice for the exact same kind of access. The same shapes against a symbol
// defined in `.text` itself (a multi-function object's own helper functions: their `call` sites
// and their addresses taken for closure code words) resolve against `.text`'s own base on either
// path, and never count as imports. Relocations whose patch site lies inside `.rodata` itself
// (`.rela.rodata`: the absolute `.text` block addresses of a `switch` jump table, which LLVM emits
// for a `SwitchTag` over enough constructors) are applied to the `.rodata` bytes with the same
// formulas once both sections have their final addresses.
// - No `R_X86_64_PLT32` relocations against an import: a fully static, non-PIE executable — one `R+X` `PT_LOAD`
//   segment for `.text`, plus a second, read-only `PT_LOAD` for `.rodata` when the object has one.
// - `R_X86_64_PLT32` relocations against a symbol `linuxDynamicImportLibraries` recognizes (the
//   narrow set `AshesCompiler.Backend.IrCodegen` can actually call today — `malloc`/`free`/`memcmp`/
//   `memcpy`): eager
//   (non-lazy) dynamic linking — a `jmp`-through-GOT stub per import, a second `R+W` `PT_LOAD`
//   data segment, `PT_INTERP`/`PT_DYNAMIC`, and the ELF hash/`.dynstr`/`.dynsym`/`.rela.dyn`
//   machinery the dynamic loader needs to resolve them, plus a THIRD, read-only `PT_LOAD` for
//   `.rodata` when the object also has one — any program that both allocates a record and prints a
//   string literal needs both together. Any relocation neither path can resolve correctly (an
//   unrecognized type, or a symbol not in the known-library table) is an `Error`, never a silently
//   wrong link. Both paths place the same 20-byte process-entry trampoline the real linker
//   (`LlvmImageLinkerElf.cs`) does at the start of `.text` (see `buildLinuxTrampoline`), so the
//   entry function is reached by a real `call` with the stack alignment LLVM-compiled code
//   assumes. The real linker additionally supports TLS sections and recognizes a much larger
//   library table; neither exists here yet.
//
// ELF field offsets and values below are taken directly from `LlvmImageLinkerElf.cs`'s own
// `WriteElf64Header`/`WriteElf64ProgramHeader`/`ParseElfObject`/`CollectLinuxDynamicImports`/
// `BuildLinuxDynamicImportLayout`/`BuildLinuxElfHash`, not invented independently: the same base
// virtual address (`0x400000`, one page below where `.text` is placed), the same
// symbol-table-driven entry-offset lookup, and the same eager-binding GOT/PLT-stub design.

import Ashes.Byte
import Ashes.Number.UInt
import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
export (
    value linkLinuxExecutable,
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

// A `.text` reference to a symbol not defined anywhere in this object (`symSectionIndex == 0`)
// whose name is a known libc entry point — the narrow set `AshesCompiler.Backend.IrCodegen` can
// actually call today. `symbolIndex` is 1-based (dynsym/hash-table index 0 is always the reserved
// null entry) and assigned in symbol-table scan order — deterministic for a fixed input object,
// and there's no requirement it match any particular order beyond internal self-consistency.
type LinuxDynamicImport =
    | symbolName: Str
    | libraryName: Str
    | symbolIndex: Int

// Everything `linkLinuxExecutable` needs to place the whole dynamic-linking data blob (interpreter
// path, hash table, `.dynstr`, `.dynsym`, GOT, `.rela.dyn`, `.dynamic`) and wire up its two extra
// program headers (`PT_INTERP`, `PT_DYNAMIC`). Offsets are relative to the start of `bytes` itself
// (the caller adds the data segment's own file offset/VA once, when writing program headers).
type LinuxDynamicImportLayout =
    | bytes: Bytes
    | gotDataOffset: Int
    | interpDataOffset: Int
    | interpByteCount: Int
    | dynamicDataOffset: Int
    | dynamicByteCount: Int

let elfHeaderSize = 64

let elfProgramHeaderSize = 56

let pageSize = 4096

// `0x400000`: the same fixed non-PIE base virtual address `LlvmImageLinkerElf.cs` uses.
let elfBaseVa = 4194304

let putU8 offset value bytes = Ashes.Byte.set(bytes)(offset)(value)

let putU16 offset value bytes = Ashes.Byte.setU16Le(bytes)(offset)(value)

let putU32 offset value bytes = Ashes.Byte.setU32Le(bytes)(offset)(value)

let putU64 offset value bytes = Ashes.Byte.setU64Le(bytes)(offset)(value)

// `Ashes.Number.UInt` only converts `Int -> u8` (`fromInt`, masking) and `Int -> u64`
// (`fromInt64`, a same-width bit-reinterpret) — there is no `Int -> u32` conversion, so a
// genuinely computed (not compile-time-literal) 32-bit field can't go through `putU32` at all.
// Every dynamic-linking field that needs one (string-table offsets, ELF hash table words) is
// written a byte at a time instead: `fromInt`'s own mod-256 truncation applied to each
// successively-shifted byte reproduces the exact little-endian 4-byte encoding regardless of the
// value's sign (only the low 32 bits of `value` are ever significant here, matching what a
// `checked((int)...)` cast would also enforce in the real C# linker).
let putU32FromInt offset value bytes =
    bytes
    |> putU8(offset)(Ashes.Number.UInt.fromInt(value))
    |> putU8(offset + 1)(Ashes.Number.UInt.fromInt(value >> 8))
    |> putU8(offset + 2)(Ashes.Number.UInt.fromInt(value >> 16))
    |> putU8(offset + 3)(Ashes.Number.UInt.fromInt(value >> 24))

let recursive listLength items =
    match items with
        | [] -> 0
        | _ :: rest -> 1 + listLength(rest)

let alignUp value alignment = (value + alignment - 1) / alignment * alignment

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

// The narrow set of libc entry points `AshesCompiler.Backend.IrCodegen` can actually call today
// (an RC-managed `AllocAdt`'s `malloc` and its eventual `free`; `memcmp` for `CmpStrEq`/`CmpStrNe`'s
// byte-payload comparison; `memcpy` for `ConcatStr`/`ConcatStrN`'s payload-copy into a freshly
// `malloc`'d result). Grown alongside `IrCodegen`'s own external-call coverage, the same "cover
// exactly what's verified, panic/error on anything else" discipline every other slice in this arc
// uses — an unrecognized external symbol is a linker `Error`, never a silently-ignored or
// mis-resolved relocation.
let linuxDynamicImportLibraries = [("malloc", "libc.so.6"), ("free", "libc.so.6"), ("memcmp", "libc.so.6"), ("memcpy", "libc.so.6")]

let recursive lookupImportLibrary symbolName knownLibraries =
    match knownLibraries with
        | [] -> None
        | (name, library) :: rest ->
            if name == symbolName
            then Some(library)
            else lookupImportLibrary(symbolName)(rest)

// Scans every symbol once (not the relocations that reference them): an object's symbol table
// lists `malloc` exactly once no matter how many call sites reference it, so this is naturally the
// right place to deduplicate. `symbolIndex` is 1-based — dynsym/hash-table slot `0` is always the
// reserved null entry.
let recursive collectLinuxDynamicImportsGo bytes symtabOffset symbolCount strtabOffset index nextIndex acc =
    if index >= symbolCount
    then reverseList(acc)
    else
        let symbol = readSymbol(bytes)(symtabOffset)(index)
        in
            if symbol.symSectionIndex != 0
            then collectLinuxDynamicImportsGo(bytes)(symtabOffset)(symbolCount)(strtabOffset)(index + 1)(nextIndex)(acc)
            else
                let name = readElfString(bytes)(strtabOffset + symbol.symNameOffset)
                in
                    match lookupImportLibrary(name)(linuxDynamicImportLibraries) with
                        | None -> collectLinuxDynamicImportsGo(bytes)(symtabOffset)(symbolCount)(strtabOffset)(index + 1)(nextIndex)(acc)
                        | Some(library) ->
                            collectLinuxDynamicImportsGo(bytes)(symtabOffset)(symbolCount)(strtabOffset)(index + 1)(nextIndex + 1)(
                                LinuxDynamicImport(symbolName = name, libraryName = library, symbolIndex = nextIndex) :: acc
                            )

let collectLinuxDynamicImports bytes symtabOffset symbolCount strtabOffset = collectLinuxDynamicImportsGo(bytes)(symtabOffset)(symbolCount)(strtabOffset)(0)(1)([])

type TextRelocationPatch =
    | patchOffset: Int
    | patchSymbolName: Str
    | patchAddend: Int

// A reference from `.text` into `.rodata` — taking the address of a string-literal global, or
// loading straight through it, compiles to one of three shapes depending on what else is in the
// same function and how the value is used downstream, all confirmed via `readelf -r`/`objdump -dr`
// on real emitted objects rather than assumed: a 4-byte absolute `R_X86_64_32`/`R_X86_64_32S`
// (`S + A`, no patch-site subtraction — a bare `Ashes.IO.print("hello")` loading `.rodata`'s `len`
// field, `mov reg, [disp32]`, no RIP), a 4-byte PC-relative `R_X86_64_PC32` (`S + A - P`, `P` the
// patch site's own final virtual address — the SAME `len`-field load once the function also calls
// `malloc`/`free`, `mov reg, [rip+disp32]`), or a full 8-byte absolute `R_X86_64_64` (`S + A`,
// written as a 64-bit little-endian word, never truncated to 32 bits since the target virtual
// address is not guaranteed to fit — a `let s = "hello"` binding materializes the string's raw
// `.rodata` address via `movabs $imm64, reg` before adding the header-size offset separately at
// runtime, rather than folding the offset into a 32-bit-addend load the way an immediately-used
// literal does). None of the three LLVM instruction-selection triggers were pinned down precisely
// (plausibly register-allocation/code-model pressure, or whether the address is consumed
// immediately vs. stored); what's certain is all three are legitimate output and the linker must
// patch whichever one it sees correctly, not assume away the ones it hadn't seen yet.
// `dataPatchPcRelative`/`dataPatchWidth` record which formula and byte width a given patch needs.
// The same four shapes also arise for a `.text` reference into `.text` itself once an object
// defines more than one function: a `call` to a locally-defined helper (`R_X86_64_PLT32` against a
// symbol whose section IS `.text`, resolved PC-relative exactly like an import stub call, just
// against the helper's own final address instead of a stub's), and a closure's code word
// (`MakeClosure` materializing the helper's address via `movabs`/`mov $imm32` — an absolute
// `R_X86_64_64`/`R_X86_64_32`/`R_X86_64_32S` against that same locally-defined function symbol).
// `dataPatchTargetsText` records which section's final base address the patch resolves against;
// `dataPatchAddend` already folds in the referenced symbol's own `st_value` (its offset within
// its section — `0` for a section symbol like `.rodata`'s, the function's offset for a named
// function symbol), so applying a patch never needs the symbol table again.
type DataRelocationPatch =
    | dataPatchOffset: Int
    | dataPatchAddend: Int
    | dataPatchPcRelative: Bool
    | dataPatchWidth: Int
    | dataPatchTargetsText: Bool

type CollectedTextRelocations =
    | functionPatches: List(TextRelocationPatch)
    | dataPatches: List(DataRelocationPatch)

// Validates and collects every `.text` relocation into `CollectedTextRelocations` in one pass: any
// relocation this narrow linker cannot resolve correctly (a type other than `R_X86_64_PLT32`
// against a known external symbol, or `R_X86_64_64`/`R_X86_64_32`/`R_X86_64_32S`/`R_X86_64_PC32`
// against `.rodata`) becomes an `Error` immediately rather than a silently wrong link. `PLT32`
// resolves identically to a rodata-targeted `PC32` for the eager (non-lazy) binding style here
// (`S + A - P`, matching `LlvmImageLinkerElf.cs`'s own `ApplyElfTextRelocationsPatch`) — the same
// math every PC-relative x86-64 call/jump/load relocation uses; only the symbol each resolves
// against differs (an import's stub VA vs. `.rodata`'s own final VA). `rodataSectionIndex` is
// `None` when the object has no `.rodata` section at all (most programs, which reference no
// string literal or other embedded constant), in which case none of the four rodata-relocation
// types can ever legitimately appear against `.rodata` and all still fall through to the same
// `Error`. Any of these five types against a symbol defined in `.text` itself (a locally-defined
// helper function: its `call` sites, and its address taken for a closure's code word) is resolved
// against `.text`'s own final base address instead — see `textTargetedPatch`.
let isRodataRelocationType relocationType =
    if relocationType == 1
    then true
    else
        if relocationType == 2
        then true
        else
            if relocationType == 10
            then true
            else relocationType == 11

let isPcRelativeRodataRelocationType relocationType = relocationType == 2

// `R_X86_64_64` is the one 8-byte rodata relocation this linker resolves; every other recognized
// type (`R_X86_64_32`/`R_X86_64_32S`/`R_X86_64_PC32`) patches a 4-byte field.
let rodataRelocationWidth relocationType =
    if relocationType == 1
    then 8
    else 4

let unsupportedRelocationMessage relocationType =
    "dynamic linker: unsupported .text relocation type " + Ashes.Text.fromInt(
        relocationType
    ) + " (only R_X86_64_PLT32 to a known external symbol, or R_X86_64_64/R_X86_64_32/R_X86_64_32S/R_X86_64_PC32 to .rodata, is supported)"

let readRelaEntryFields bytes entryOffset =
    (let relocOffset = getU64(bytes)(entryOffset)
    in
        let info = getU64(bytes)(entryOffset + 8)
        in
            let addend = getU64(bytes)(entryOffset + 16)
            in (relocOffset, info >> 32, info & 4294967295, addend))

// A reference whose symbol lives in `.text` itself — a locally-defined helper function, whether
// reached by a `call` (`PLT32`, always PC-relative) or by materializing its address for a
// closure's code word (any of the absolute/PC-relative data shapes). The symbol's own `st_value`
// is its offset within `.text`, folded into the addend so `applyDataPatches` resolves it against
// `.text`'s final base address alone.
let textTargetedPatch relocOffset relocationType symbolValue addend =
    DataRelocationPatch(
        dataPatchOffset = relocOffset,
        dataPatchAddend = addend + symbolValue,
        dataPatchPcRelative = relocationType == 4 || isPcRelativeRodataRelocationType(relocationType),
        dataPatchWidth = rodataRelocationWidth(relocationType),
        dataPatchTargetsText = true
    )

let rodataTargetedPatch relocOffset relocationType symbolValue addend =
    DataRelocationPatch(
        dataPatchOffset = relocOffset,
        dataPatchAddend = addend + symbolValue,
        dataPatchPcRelative = isPcRelativeRodataRelocationType(relocationType),
        dataPatchWidth = rodataRelocationWidth(relocationType),
        dataPatchTargetsText = false
    )

// `.rodata` plus LLVM's read-only companions (`.rodata.cst8`/`.rodata.cst16` constant pools for
// `double` literals, `.rodata.str1.*` merged strings): every PROGBITS section whose name starts
// with `.rodata` joins one concatenated read-only image, each section placed at a 16-byte-aligned
// layout offset (plain `.rodata`, when present, keeps its position in section order — alone it
// sits at offset 0, byte-identical to the previous single-section model).
let isRodataSectionName name =
    if name == ".rodata"
    then true
    else
        if Ashes.Text.byteLength(name) > 7
        then
            Ashes.Byte.subText(Ashes.Byte.fromText(name))(0)(7) == ".rodata"
        else false

let alignToSixteen value = (value + 15) / 16 * 16

type RodataSectionLayout =
    | rodataIndex: Int
    | rodataHeader: ElfSectionHeader
    | rodataLayoutOffset: Int

let recursive collectRodataSectionLayouts bytes shoff shentsize shnum shstrtabOffset index nextOffset acc =
    if index >= shnum
    then reverseList(acc)
    else
        let section = readSectionHeader(bytes)(shoff)(shentsize)(index)
        in
            if section.sectionType != 1 || section.sectionSize == 0
            then collectRodataSectionLayouts(bytes)(shoff)(shentsize)(shnum)(shstrtabOffset)(index + 1)(nextOffset)(acc)
            else
                if shstrtabOffset + section.sectionNameOffset
                |> readElfString(bytes)
                |> isRodataSectionName
                then
                    collectRodataSectionLayouts(bytes)(shoff)(shentsize)(shnum)(shstrtabOffset)(index + 1)(
                        alignToSixteen(nextOffset + section.sectionSize)
                    )(
                        RodataSectionLayout(rodataIndex = index, rodataHeader = section, rodataLayoutOffset = nextOffset) :: acc
                    )
                else collectRodataSectionLayouts(bytes)(shoff)(shentsize)(shnum)(shstrtabOffset)(index + 1)(nextOffset)(acc)

let recursive lookupRodataLayout sectionIndex layouts =
    match layouts with
        | [] -> None
        | RodataSectionLayout { rodataIndex = candidate, rodataLayoutOffset = layoutOffset } :: rest ->
            if candidate == sectionIndex
            then Some(layoutOffset)
            else lookupRodataLayout(sectionIndex)(rest)

let recursive rodataImageSize layouts =
    match layouts with
        | [] -> 0
        | RodataSectionLayout { rodataHeader = section, rodataLayoutOffset = layoutOffset } :: rest ->
            let candidate = layoutOffset + section.sectionSize
            in
                let restSize = rodataImageSize(rest)
                in
                    if candidate > restSize
                    then candidate
                    else restSize

let recursive copyRodataSections objectBytes layouts image =
    match layouts with
        | [] -> image
        | RodataSectionLayout { rodataHeader = section, rodataLayoutOffset = layoutOffset } :: rest ->
            copyRodataSections(objectBytes)(rest)(
                Ashes.Byte.copyRange(image)(layoutOffset)(objectBytes)(section.sectionOffset)(section.sectionSize)
            )

let buildRodataImage objectBytes layouts =
    match layouts with
        | [] -> None
        | _ ->
            layouts
            |> rodataImageSize
            |> Ashes.Byte.allocate
            |> copyRodataSections(objectBytes)(layouts)
            |> Some

let recursive collectRelaEntryPatches bytes section symtabOffset strtabOffset textSectionIndex rodataLayouts patchBaseOffset entryIndex entryCount functionAcc dataAcc =
    if entryIndex >= entryCount
    then Ok(CollectedTextRelocations(functionPatches = functionAcc, dataPatches = dataAcc))
    else
        let entryOffset = section.sectionOffset + entryIndex * 24
        in
            match readRelaEntryFields(bytes)(entryOffset) with
                | (relocOffset, symbolIndex, relocationType, addend) ->
                    let symbol = readSymbol(bytes)(symtabOffset)(symbolIndex)
                    in
                        if symbol.symSectionIndex == textSectionIndex && (relocationType == 4 || isRodataRelocationType(relocationType))
                        then
                            collectRelaEntryPatches(bytes)(section)(symtabOffset)(strtabOffset)(textSectionIndex)(rodataLayouts)(patchBaseOffset)(entryIndex + 1)(entryCount)(functionAcc)(
                                textTargetedPatch(patchBaseOffset + relocOffset)(relocationType)(symbol.symValue)(addend) :: dataAcc
                            )
                        else
                            if relocationType == 4
                            then
                                if symbol.symSectionIndex != 0
                                then Error("dynamic linker: PLT32 relocation targets a locally-defined symbol outside .text, which is not supported")
                                else
                                    let symbolName = readElfString(bytes)(strtabOffset + symbol.symNameOffset)
                                    in
                                        match lookupImportLibrary(symbolName)(linuxDynamicImportLibraries) with
                                            | None -> Error("dynamic linker: unknown external symbol '" + symbolName + "' (not in the recognized-library table)")
                                            | Some(_) ->
                                                collectRelaEntryPatches(bytes)(section)(symtabOffset)(strtabOffset)(textSectionIndex)(rodataLayouts)(patchBaseOffset)(entryIndex + 1)(entryCount)(
                                                    TextRelocationPatch(patchOffset = patchBaseOffset + relocOffset, patchSymbolName = symbolName, patchAddend = addend) :: functionAcc
                                                )(dataAcc)
                            else
                                if isRodataRelocationType(relocationType)
                                then
                                    match lookupRodataLayout(symbol.symSectionIndex)(rodataLayouts) with
                                        | Some(layoutOffset) ->
                                            collectRelaEntryPatches(bytes)(section)(symtabOffset)(strtabOffset)(textSectionIndex)(rodataLayouts)(patchBaseOffset)(entryIndex + 1)(entryCount)(functionAcc)(
                                                rodataTargetedPatch(patchBaseOffset + relocOffset)(relocationType)(symbol.symValue + layoutOffset)(addend) :: dataAcc
                                            )
                                        | None ->
                                            relocationType
                                            |> unsupportedRelocationMessage
                                            |> Error
                                else
                                    relocationType
                                    |> unsupportedRelocationMessage
                                    |> Error

let recursive collectTextPatches bytes shoff shentsize shnum textSectionIndex symtabOffset strtabOffset rodataLayouts index functionAcc dataAcc =
    if index >= shnum
    then Ok(CollectedTextRelocations(functionPatches = reverseList(functionAcc), dataPatches = reverseList(dataAcc)))
    else
        let section = readSectionHeader(bytes)(shoff)(shentsize)(index)
        in
            if section.sectionSize == 0
            then collectTextPatches(bytes)(shoff)(shentsize)(shnum)(textSectionIndex)(symtabOffset)(strtabOffset)(rodataLayouts)(index + 1)(functionAcc)(dataAcc)
            else
                if section.sectionInfo != textSectionIndex
                then collectTextPatches(bytes)(shoff)(shentsize)(shnum)(textSectionIndex)(symtabOffset)(strtabOffset)(rodataLayouts)(index + 1)(functionAcc)(dataAcc)
                else
                    match section.sectionType with
                        | 4 ->
                            match collectRelaEntryPatches(bytes)(section)(symtabOffset)(strtabOffset)(textSectionIndex)(rodataLayouts)(0)(0)(section.sectionSize / 24)(functionAcc)(dataAcc) with
                                | Error(message) -> Error(message)
                                | Ok(CollectedTextRelocations { functionPatches = nextFunctionAcc, dataPatches = nextDataAcc }) -> collectTextPatches(bytes)(shoff)(shentsize)(shnum)(textSectionIndex)(symtabOffset)(strtabOffset)(rodataLayouts)(index + 1)(nextFunctionAcc)(nextDataAcc)
                        | 9 -> Error("dynamic linker: SHT_REL (implicit-addend) .text relocations are not supported")
                        | _ -> collectTextPatches(bytes)(shoff)(shentsize)(shnum)(textSectionIndex)(symtabOffset)(strtabOffset)(rodataLayouts)(index + 1)(functionAcc)(dataAcc)

// Every relocation whose patch site lies inside `.rodata` (the entries of a `switch` jump table:
// `R_X86_64_64` against `.text`, occasionally a rodata-to-rodata reference), collected with the
// same entry validation as the `.text` relocations. Offsets are relative to `.rodata`'s own bytes.
// An import relocation cannot appear inside read-only data, so one is an `Error` rather than a
// patch this path could never apply.
let recursive collectRodataPatches bytes shoff shentsize shnum textSectionIndex symtabOffset strtabOffset rodataLayouts index dataAcc =
    match rodataLayouts with
        | [] ->
            dataAcc
            |> reverseList
            |> Ok
        | _ ->
            if index >= shnum
            then
                dataAcc
                |> reverseList
                |> Ok
            else
                let section = readSectionHeader(bytes)(shoff)(shentsize)(index)
                in
                    if section.sectionSize == 0 || section.sectionType != 4
                    then collectRodataPatches(bytes)(shoff)(shentsize)(shnum)(textSectionIndex)(symtabOffset)(strtabOffset)(rodataLayouts)(index + 1)(dataAcc)
                    else
                        match lookupRodataLayout(section.sectionInfo)(rodataLayouts) with
                            | None -> collectRodataPatches(bytes)(shoff)(shentsize)(shnum)(textSectionIndex)(symtabOffset)(strtabOffset)(rodataLayouts)(index + 1)(dataAcc)
                            | Some(targetLayoutOffset) ->
                                match collectRelaEntryPatches(bytes)(section)(symtabOffset)(strtabOffset)(textSectionIndex)(rodataLayouts)(targetLayoutOffset)(0)(section.sectionSize / 24)([])(dataAcc) with
                                    | Error(message) -> Error(message)
                                    | Ok(CollectedTextRelocations { functionPatches = [], dataPatches = nextDataAcc }) -> collectRodataPatches(bytes)(shoff)(shentsize)(shnum)(textSectionIndex)(symtabOffset)(strtabOffset)(rodataLayouts)(index + 1)(nextDataAcc)
                                    | Ok(_) -> Error("linker: an import relocation inside .rodata is not supported")

let recursive lookupStubVa symbolName stubVas =
    match stubVas with
        | [] -> Ashes.IO.panic("dynamic linker: missing stub VA for " + symbolName)
        | (name, va) :: rest ->
            if name == symbolName
            then va
            else lookupStubVa(symbolName)(rest)

let recursive applyTextPatches patches stubVas textVa codeBytes =
    match patches with
        | [] -> codeBytes
        | TextRelocationPatch { patchOffset = patchOffset, patchSymbolName = patchSymbolName, patchAddend = patchAddend } :: rest ->
            let placeVa = textVa + patchOffset
            in
                let value = lookupStubVa(patchSymbolName)(stubVas) + patchAddend - placeVa
                in
                    codeBytes
                    |> putU32FromInt(patchOffset)(value)
                    |> applyTextPatches(rest)(stubVas)(textVa)

// A `.text` reference into `.rodata` or into `.text` itself, either absolute (`S + A`, no
// patch-site subtraction) or PC-relative (`S + A - P`, `P` the patch site's own final virtual
// address, `textVa + patchOffset` — the same formula `applyTextPatches` uses for a call/jump stub;
// only the 4-byte types use this, an 8-byte `R_X86_64_64` is always absolute). `rodataVa`/`textVa`
// are each section's own final base address once laid out; every collected patch resolves against
// one of those two (`dataPatchTargetsText` says which) with the symbol's own section offset
// already folded into its addend, so unlike `applyTextPatches` there is no per-symbol lookup at
// all. An 8-byte patch writes the full computed virtual address (`putU64`) rather than truncating
// to 32 bits (`putU32FromInt`) the way every 4-byte type does. `placeVa` is the final base address
// of `bytes` itself — `.text`'s when patching code, `.rodata`'s when patching a jump table — so a
// PC-relative patch subtracts the patch site's own final address whichever section holds it.
let recursive applyDataPatches patches rodataVa textVa placeVa bytes =
    match patches with
        | [] -> bytes
        | DataRelocationPatch { dataPatchOffset = patchOffset, dataPatchAddend = patchAddend, dataPatchPcRelative = pcRelative, dataPatchWidth = width, dataPatchTargetsText = targetsText } :: rest ->
            let targetVa =
                if targetsText
                then textVa + patchAddend
                else rodataVa + patchAddend
            in
                let value =
                    if pcRelative
                    then targetVa - (placeVa + patchOffset)
                    else targetVa
                in
                    let patched =
                        if width == 8
                        then
                            putU64(patchOffset)(Ashes.Number.UInt.fromInt64(value))(bytes)
                        else putU32FromInt(patchOffset)(value)(bytes)
                    in applyDataPatches(rest)(rodataVa)(textVa)(placeVa)(patched)

let linuxDynamicLoaderPath = "/lib64/ld-linux-x86-64.so.2"

// Every dynamically-linked Ashes executable searches its own directory first — a `$ORIGIN`
// `DT_RUNPATH` — so a program can resolve a library placed next to it without depending on the
// host's install state, matching `LlvmImageLinkerElf.cs`'s own `LinuxDynamicRunPath`.
let linuxDynamicRunPath = "$ORIGIN"

let linuxImportStubLength = 6

let elfRelocX86_64GlobDat = 6

// Appends `chunk` to `bytes`, then zero-pads up to the next 8-byte boundary — every piece of the
// dynamic-linking data blob (hash table, `.dynstr`, `.dynsym`, GOT, `.rela.dyn`, `.dynamic`) is
// 8-byte aligned, matching `LlvmImageLinkerElf.cs`'s own `AlignImportStream`. Returns the new bytes
// together with `chunk`'s own (unpadded) start offset.
let appendAligned bytes chunk =
    (let offset = Ashes.Byte.length(bytes)
    in
        let withChunk = Ashes.Byte.append(bytes)(chunk)
        in
            let paddedLength =
                alignUp(Ashes.Byte.length(withChunk))(8)
            in
                (paddedLength - Ashes.Byte.length(withChunk)
                |> Ashes.Byte.allocate
                |> Ashes.Byte.append(withChunk), offset))

let recursive importSymbolNames imports =
    match imports with
        | [] -> []
        | LinuxDynamicImport { symbolName = symbolName } :: rest -> symbolName :: importSymbolNames(rest)

let recursive containsStr value items =
    match items with
        | [] -> false
        | head :: rest ->
            if head == value
            then true
            else containsStr(value)(rest)

let recursive distinctLibrariesGo imports acc =
    match imports with
        | [] -> reverseList(acc)
        | LinuxDynamicImport { libraryName = libraryName } :: rest ->
            if containsStr(libraryName)(acc)
            then distinctLibrariesGo(rest)(acc)
            else distinctLibrariesGo(rest)(libraryName :: acc)

let distinctLibraries imports = distinctLibrariesGo(imports)([])

let appendNulString bytes text =
    (let offset = Ashes.Byte.length(bytes)
    in
        (Ashes.Byte.appendByte(text
        |> Ashes.Byte.fromText
        |> Ashes.Byte.append(bytes))(0u8), offset))

let recursive appendNulStrings names bytes offsetsAcc =
    match names with
        | [] -> (bytes, offsetsAcc)
        | name :: rest ->
            match appendNulString(bytes)(name) with
                | (nextBytes, offset) -> appendNulStrings(rest)(nextBytes)((name, offset) :: offsetsAcc)

let recursive lookupStrtabOffset name offsets =
    match offsets with
        | [] -> Ashes.IO.panic("dynamic linker: missing .dynstr offset for " + name)
        | (candidateName, offset) :: rest ->
            if candidateName == name
            then offset
            else lookupStrtabOffset(name)(rest)

// `.dynstr` layout: byte `0` reserved as the empty string (matching every other ELF string table
// in this file), then `$ORIGIN` (the `DT_RUNPATH` value), then each distinct library name (for
// `DT_NEEDED`), then each import's own symbol name (for `.dynsym`).
let buildDynstrTable imports =
    (let libraries = distinctLibraries(imports)
    in
        match appendNulString(Ashes.Byte.singleton(0u8))(linuxDynamicRunPath) with
            | (afterRunPath, runPathOffset) ->
                match appendNulStrings(libraries)(afterRunPath)([(linuxDynamicRunPath, runPathOffset)]) with
                    | (afterLibraries, offsetsAfterLibraries) ->
                        match appendNulStrings(importSymbolNames(imports))(afterLibraries)(offsetsAfterLibraries) with
                            | (dynstrBytes, allOffsets) -> (libraries, dynstrBytes, allOffsets))

// `STT_FUNC` (`2`) + `STB_GLOBAL` (`1`, high nibble) — `st_info = (1 << 4) | 2 = 18`. Every import
// is genuinely undefined (`st_shndx = SHN_UNDEF = 0`) with no size/value of its own; the dynamic
// loader fills in where it actually lives at load time.
let recursive buildDynamicSymbolTableGo imports dynstrOffsets index bytes =
    match imports with
        | [] -> bytes
        | LinuxDynamicImport { symbolName = symbolName } :: rest ->
            let entryOffset = index * 24
            in
                bytes
                |> putU32FromInt(entryOffset)(lookupStrtabOffset(symbolName)(dynstrOffsets))
                |> putU8(entryOffset + 4)(18u8)
                |> putU8(entryOffset + 5)(0u8)
                |> putU16(entryOffset + 6)(0u16)
                |> putU64(entryOffset + 8)(0u64)
                |> putU64(entryOffset + 16)(0u64)
                |> buildDynamicSymbolTableGo(rest)(dynstrOffsets)(index + 1)

let buildDynamicSymbolTable imports dynstrOffsets =
    (listLength(imports) + 1) * 24
    |> Ashes.Byte.allocate
    |> buildDynamicSymbolTableGo(imports)(dynstrOffsets)(1)

// One `R_X86_64_GLOB_DAT` relocation per GOT entry: the dynamic loader writes the resolved symbol
// address directly into `gotVa + i*8` at load time, no lazy PLT0/resolver trampoline involved
// (the eager-binding style `LlvmImageLinkerElf.cs` itself uses).
let recursive buildGlobalDataRelocationsGo imports gotVa index bytes =
    match imports with
        | [] -> bytes
        | LinuxDynamicImport { symbolIndex = symbolIndex } :: rest ->
            (let entryOffset = index * 24
            in
                bytes
                |> putU64(entryOffset)(Ashes.Number.UInt.fromInt64(gotVa + index * 8))
                |> putU64(entryOffset + 8)(Ashes.Number.UInt.fromInt64(symbolIndex << 32 | elfRelocX86_64GlobDat))
                |> putU64(entryOffset + 16)(0u64)
                |> buildGlobalDataRelocationsGo(rest)(gotVa)(index + 1))

let buildGlobalDataRelocations imports gotVa =
    listLength(imports) * 24
    |> Ashes.Byte.allocate
    |> buildGlobalDataRelocationsGo(imports)(gotVa)(0)

// The classic SysV ELF hash function (`elf_hash` in the gABI): a simple rolling hash over the
// symbol name's bytes, folding any bits that would overflow 32 bits back in via XOR rather than
// discarding them. Matches `LlvmImageLinkerElf.cs`'s own `ElfHash` exactly.
let recursive elfHashGo bytes index count hash =
    if index >= count
    then hash
    else
        let shifted = (hash << 4) + getU8(bytes)(index)
        in
            let masked = shifted & 4294967295
            in
                let x = masked & 4026531840
                in
                    let hash2 =
                        if x != 0
                        then masked ^ x >> 24
                        else masked
                    in elfHashGo(bytes)(index + 1)(count)(hash2 & ~x)

let elfHash text =
    (let bytes = Ashes.Byte.fromText(text)
    in
        elfHashGo(bytes)(0)(Ashes.Byte.length(bytes))(0))

let recursive symbolsInBucket imports nbucket bucketIndex =
    match imports with
        | [] -> []
        | LinuxDynamicImport { symbolName = symbolName, symbolIndex = symbolIndex } :: rest ->
            if elfHash(symbolName) % nbucket == bucketIndex
            then symbolIndex :: symbolsInBucket(rest)(nbucket)(bucketIndex)
            else symbolsInBucket(rest)(nbucket)(bucketIndex)

// Consecutive same-bucket symbols chain together (`chain[a] = b`); the last one in a bucket keeps
// the default `0` chain-slot value as its terminator, matching the SysV hash table format exactly.
let recursive chainPairsFor symbolIndices =
    match symbolIndices with
        | [] -> []
        | _ :: [] -> []
        | a :: (b :: _ as rest) -> (a, b) :: chainPairsFor(rest)

let recursive buildBucketsAndChainPairs imports nbucket bucketIndex bucketsAcc chainPairsAcc =
    if bucketIndex >= nbucket
    then (reverseList(bucketsAcc), chainPairsAcc)
    else
        let symbolIndices = symbolsInBucket(imports)(nbucket)(bucketIndex)
        in
            let bucketHead =
                match symbolIndices with
                    | [] -> 0
                    | first :: _ -> first
            in
                buildBucketsAndChainPairs(imports)(nbucket)(bucketIndex + 1)(bucketHead :: bucketsAcc)(
                    appendList(chainPairsFor(symbolIndices))(chainPairsAcc)
                )

let recursive chainValueAt chainPairs index =
    match chainPairs with
        | [] -> 0
        | (from, to_) :: rest ->
            if from == index
            then to_
            else chainValueAt(rest)(index)

let recursive writeWords values index bytes =
    match values with
        | [] -> bytes
        | value :: rest ->
            bytes
            |> putU32FromInt(index * 4)(value)
            |> writeWords(rest)(index + 1)

let recursive buildChainValues chainPairs nchain index acc =
    if index >= nchain
    then reverseList(acc)
    else buildChainValues(chainPairs)(nchain)(index + 1)(chainValueAt(chainPairs)(index) :: acc)

// SysV `.hash` layout: `nbucket`, `nchain`, then `nbucket` bucket words, then `nchain` chain
// words. `nbucket == max(1, importCount)`, `nchain == importCount + 1` (slot `0` reserved),
// matching `LlvmImageLinkerElf.cs`'s own `BuildLinuxElfHash`.
let buildLinuxElfHash imports =
    (let importCount = listLength(imports)
    in
        let nbucket =
            if importCount > 1
            then importCount
            else 1
        in
            let nchain = importCount + 1
            in
                match buildBucketsAndChainPairs(imports)(nbucket)(0)([])([]) with
                    | (buckets, chainPairs) ->
                        (2 + nbucket + nchain) * 4
                        |> Ashes.Byte.allocate
                        |> putU32FromInt(0)(nbucket)
                        |> putU32FromInt(4)(nchain)
                        |> writeWords(buckets)(2)
                        |> writeWords(buildChainValues(chainPairs)(nchain)(0)([]))(2 + nbucket))

let recursive buildDynamicEntries entries index bytes =
    match entries with
        | [] -> bytes
        | (tag, value) :: rest ->
            let entryOffset = index * 16
            in
                bytes
                |> putU64(entryOffset)(Ashes.Number.UInt.fromInt64(tag))
                |> putU64(entryOffset + 8)(Ashes.Number.UInt.fromInt64(value))
                |> buildDynamicEntries(rest)(index + 1)

let recursive neededEntries libraries dynstrOffsets =
    match libraries with
        | [] -> []
        | library :: rest -> (1, lookupStrtabOffset(library)(dynstrOffsets)) :: neededEntries(rest)(dynstrOffsets)

// `DT_NEEDED` per distinct library, `DT_RUNPATH`, then the hash/string/symbol/relocation table
// descriptors every dynamic loader needs to resolve this executable's imports, terminated by
// `DT_NULL` — matching `LlvmImageLinkerElf.cs`'s own `BuildLinuxDynamicTable` tag-for-tag.
let buildDynamicTable libraries dynstrOffsets hashVa dynstrVa dynstrSize dynsymVa relaVa relaSize =
    (let entries =
        appendList(neededEntries(libraries)(dynstrOffsets))(
            [
                (29, lookupStrtabOffset(linuxDynamicRunPath)(dynstrOffsets)),
                (4, hashVa),
                (5, dynstrVa),
                (6, dynsymVa),
                (10, dynstrSize),
                (11, 24),
                (7, relaVa),
                (8, relaSize),
                (9, 24),
                (0, 0)
            ]
        )
    in
        listLength(entries) * 16
        |> Ashes.Byte.allocate
        |> buildDynamicEntries(entries)(0))

// Lays out the whole dynamic-linking data blob (offsets relative to its own start — the caller
// adds the data segment's file offset/VA once): interpreter path, hash table, `.dynstr`,
// `.dynsym`, GOT (zero-filled; the loader populates it via `.rela.dyn` at load time), `.rela.dyn`,
// `.dynamic` — in that exact order, matching `LlvmImageLinkerElf.cs`'s own
// `BuildLinuxDynamicImportLayout`.
let buildDynamicImportLayout imports dataVa =
    (let interpBytes =
        Ashes.Byte.appendByte(Ashes.Byte.fromText(linuxDynamicLoaderPath))(0u8)
    in
        match appendAligned(Ashes.Byte.allocate(0))(interpBytes) with
            | (afterInterp, interpDataOffset) ->
                match buildDynstrTable(imports) with
                    | (libraries, dynstrBytes, dynstrOffsets) ->
                        match imports
                        |> buildLinuxElfHash
                        |> appendAligned(afterInterp) with
                            | (afterHash, hashDataOffset) ->
                                match appendAligned(afterHash)(dynstrBytes) with
                                    | (afterDynstr, dynstrDataOffset) ->
                                        match dynstrOffsets
                                        |> buildDynamicSymbolTable(imports)
                                        |> appendAligned(afterDynstr) with
                                            | (afterDynsym, dynsymDataOffset) ->
                                                match listLength(imports) * 8
                                                |> Ashes.Byte.allocate
                                                |> appendAligned(afterDynsym) with
                                                    | (afterGot, gotDataOffset) ->
                                                        let relaBytes = buildGlobalDataRelocations(imports)(dataVa + gotDataOffset)
                                                        in
                                                            match appendAligned(afterGot)(relaBytes) with
                                                                | (afterRela, relaDataOffset) ->
                                                                    let dynamicBytes =
                                                                        buildDynamicTable(libraries)(dynstrOffsets)(dataVa + hashDataOffset)(
                                                                            dataVa + dynstrDataOffset
                                                                        )(Ashes.Byte.length(dynstrBytes))(dataVa + dynsymDataOffset)(dataVa + relaDataOffset)(
                                                                            Ashes.Byte.length(relaBytes)
                                                                        )
                                                                    in
                                                                        match appendAligned(afterRela)(dynamicBytes) with
                                                                            | (finalBytes, dynamicDataOffset) ->
                                                                                LinuxDynamicImportLayout(
                                                                                    bytes = finalBytes,
                                                                                    gotDataOffset = gotDataOffset,
                                                                                    interpDataOffset = interpDataOffset,
                                                                                    interpByteCount = Ashes.Byte.length(interpBytes),
                                                                                    dynamicDataOffset = dynamicDataOffset,
                                                                                    dynamicByteCount = Ashes.Byte.length(dynamicBytes)
                                                                                ))

// `FF 25 <disp32>` (`jmp *disp32(%rip)`): the stub reads the resolved function address straight
// out of its GOT slot and jumps to it. No lazy PLT0/resolver stub — the GOT is already fully
// populated by the dynamic loader before this executable's entry point ever runs (eager binding),
// matching `LlvmImageLinkerElf.cs`'s own `BuildLinuxDynamicImportLayoutStubs`.
let recursive buildImportStubsGo imports stubBaseVa gotVa index bytes stubVasAcc =
    match imports with
        | [] -> (bytes, stubVasAcc)
        | LinuxDynamicImport { symbolName = symbolName } :: rest ->
            let stubOffset = index * linuxImportStubLength
            in
                let stubVa = stubBaseVa + stubOffset
                in
                    let disp32 = gotVa + index * 8 - (stubVa + linuxImportStubLength)
                    in
                        buildImportStubsGo(rest)(stubBaseVa)(gotVa)(index + 1)(
                            bytes
                            |> putU8(stubOffset)(255u8)
                            |> putU8(stubOffset + 1)(37u8)
                            |> putU32FromInt(stubOffset + 2)(disp32)
                        )((symbolName, stubVa) :: stubVasAcc)

let buildImportStubs imports stubBaseVa gotVa =
    buildImportStubsGo(imports)(stubBaseVa)(gotVa)(0)(Ashes.Byte.allocate(listLength(imports) * linuxImportStubLength))([])

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

let putU16FromInt offset value bytes =
    bytes
    |> putU8(offset)(Ashes.Number.UInt.fromInt(value))
    |> putU8(offset + 1)(Ashes.Number.UInt.fromInt(value >> 8))

let writeElfHeaderFields entryPoint programHeaderCount bytes =
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
    |> putU16FromInt(56)(programHeaderCount)
    |> putU16(58)(0u16)
    |> putU16(60)(0u16)
    |> putU16(62)(0u16)

// `fileSize`/`memorySize` are always equal for every program header this linker writes (no
// BSS-style zero-fill-only region) — merged into one `size` parameter.
let writeProgramHeaderAt index type_ flags fileOffset virtualAddress size alignment bytes =
    (let base_ = elfHeaderSize + index * elfProgramHeaderSize
    in
        bytes
        |> putU32FromInt(base_ + 0)(type_)
        |> putU32FromInt(base_ + 4)(flags)
        |> putU64(base_ + 8)(Ashes.Number.UInt.fromInt64(fileOffset))
        |> putU64(base_ + 16)(Ashes.Number.UInt.fromInt64(virtualAddress))
        |> putU64(base_ + 24)(Ashes.Number.UInt.fromInt64(virtualAddress))
        |> putU64(base_ + 32)(Ashes.Number.UInt.fromInt64(size))
        |> putU64(base_ + 40)(Ashes.Number.UInt.fromInt64(size))
        |> putU64(base_ + 48)(Ashes.Number.UInt.fromInt64(alignment)))

type ProgramHeaderPlan =
    | phType: Int
    | phFlags: Int
    | phFileOffset: Int
    | phVirtualAddress: Int
    | phSize: Int
    | phAlignment: Int

let recursive writeProgramHeaderPlans plans index bytes =
    match plans with
        | [] -> bytes
        | ProgramHeaderPlan { phType = phType, phFlags = phFlags, phFileOffset = phFileOffset, phVirtualAddress = phVirtualAddress, phSize = phSize, phAlignment = phAlignment } :: rest ->
            bytes
            |> writeProgramHeaderAt(index)(phType)(phFlags)(phFileOffset)(phVirtualAddress)(phSize)(phAlignment)
            |> writeProgramHeaderPlans(rest)(index + 1)

let buildHeaderPage entryPoint programHeaderPlans =
    pageSize
    |> Ashes.Byte.allocate
    |> writeElfIdent
    |> writeElfHeaderFields(Ashes.Number.UInt.fromInt64(entryPoint))(listLength(programHeaderPlans))
    |> writeProgramHeaderPlans(programHeaderPlans)(0)

// No dynamic imports: one `PT_LOAD` segment (`R+X`) covering the whole file, headers and `.text`
// together in the first page — exactly the original static-only layout.
// `rodataBytes = None` is the original, unchanged single-segment shape (byte-identical to before
// `.rodata` support existed — several tests assert on it structurally). `Some(rodata)` adds a
// second, page-aligned, READ-ONLY `PT_LOAD` right after `.text`'s own page — never `R+W` and never
// executable, since a string literal's storage is genuinely immutable — and patches every collected
// absolute `.text` reference to point into it before the text bytes are ever written out.
// The 20-byte Linux process-entry trampoline `LlvmImageLinkerElf.cs`'s `BuildLinuxTrampoline`
// places at the very start of `.text` (so `e_entry` is `textVa` itself, and the object's own code
// begins at `textVa + linuxTrampolineLength`): `mov rdi, rsp` (the initial stack pointer, where
// argc/argv live — this entry function takes no parameters and ignores it, but the register
// convention is kept identical), `call <entry>` (`rel32 = 20 + entryOffsetInText - 8`, relative
// to the byte after the 8-byte prefix+call), then `mov edi, 0; mov eax, 60; syscall` as a
// fallback `exit(0)` that is never reached because the entry function exits itself. The `call`
// is the whole point: the kernel starts the process with `rsp` 16-byte aligned, whereas
// LLVM-compiled code assumes the post-`call` state (`rsp ≡ 8 mod 16`) on entry — jumping straight
// to the entry function leaves every frame it builds, and every libc call it makes, misaligned
// by 8 (a latent ABI violation glibc's `malloc`/`memcmp`/`memcpy` merely happen to tolerate).
let linuxTrampolineLength = 20

let buildLinuxTrampoline entryOffsetInText =
    linuxTrampolineLength
    |> Ashes.Byte.allocate
    |> putU8(0)(72u8)
    |> putU8(1)(137u8)
    |> putU8(2)(231u8)
    |> putU8(3)(232u8)
    |> putU32FromInt(4)(linuxTrampolineLength + entryOffsetInText - 8)
    |> putU8(8)(191u8)
    |> putU32FromInt(9)(0)
    |> putU8(13)(184u8)
    |> putU32FromInt(14)(60)
    |> putU8(18)(15u8)
    |> putU8(19)(5u8)

let linkWithoutDynamicImports textBytes entrySymbol dataPatches rodataPatches rodataBytes =
    (let textVa = elfBaseVa + pageSize
    in
        let objectTextVa = textVa + linuxTrampolineLength
        in
            let entryPoint = textVa
            in
                let trampoline = buildLinuxTrampoline(entrySymbol.symValue)
                in
                    let textLoadSize = pageSize + linuxTrampolineLength + Ashes.Byte.length(textBytes)
                    in
                        match rodataBytes with
                            | None ->
                                let plan = ProgramHeaderPlan(phType = 1, phFlags = 5, phFileOffset = 0, phVirtualAddress = elfBaseVa, phSize = textLoadSize, phAlignment = pageSize)
                                in
                                    textBytes
                                    |> applyDataPatches(dataPatches)(0)(objectTextVa)(objectTextVa)
                                    |> Ashes.Byte.append(trampoline)
                                    |> Ashes.Byte.append(buildHeaderPage(entryPoint)([plan]))
                                    |> Ok
                            | Some(unpatchedRodata) ->
                                let dataFileOffset = alignUp(textLoadSize)(pageSize)
                                in
                                    let dataVa = elfBaseVa + dataFileOffset
                                    in
                                        let rodata = applyDataPatches(rodataPatches)(dataVa)(objectTextVa)(dataVa)(unpatchedRodata)
                                        in
                                            let patchedTextBytes =
                                                textBytes
                                                |> applyDataPatches(dataPatches)(dataVa)(objectTextVa)(objectTextVa)
                                                |> Ashes.Byte.append(trampoline)
                                            in
                                                let textPlan = ProgramHeaderPlan(phType = 1, phFlags = 5, phFileOffset = 0, phVirtualAddress = elfBaseVa, phSize = textLoadSize, phAlignment = pageSize)
                                                in
                                                    let dataPlan = ProgramHeaderPlan(phType = 1, phFlags = 4, phFileOffset = dataFileOffset, phVirtualAddress = dataVa, phSize = Ashes.Byte.length(rodata), phAlignment = pageSize)
                                                    in
                                                        let headerAndText =
                                                            Ashes.Byte.append(buildHeaderPage(entryPoint)([textPlan, dataPlan]))(patchedTextBytes)
                                                        in
                                                            let padded =
                                                                dataFileOffset - Ashes.Byte.length(headerAndText)
                                                                |> Ashes.Byte.allocate
                                                                |> Ashes.Byte.append(headerAndText)
                                                            in
                                                                rodata
                                                                |> Ashes.Byte.append(padded)
                                                                |> Ok)

// Dynamic imports exist: `.text` gains a `jmp *got(%rip)` stub per import (placed right after the
// object's own code, same page-aligned text segment), followed by a second, page-aligned `R+W`
// data segment holding the whole dynamic-linking blob (`PT_INTERP` + `PT_DYNAMIC` point INTO that
// segment, they do not need `PT_LOAD` entries of their own). Text relocations against the known
// external symbols are patched to point at each symbol's own stub.
// `dataPatches`/`rodataBytes` are the same pair `linkWithoutDynamicImports` takes: `rodataBytes =
// None` reproduces the original dynamic-imports-only layout exactly (no third segment, no
// behavior change for any object without a `.rodata` section). `Some(rodata)` adds a THIRD,
// page-aligned, read-only `PT_LOAD` right after the dynamic-linking data blob's own page — never
// `R+W` and never executable — and patches every collected `.text` reference into it via the same
// `applyDataPatches` the static-only path uses, absolute or PC-relative as each patch records.
let linkWithDynamicImports objectBytes textBytes entrySymbol textPatches dataPatches rodataPatches imports rodataBytes =
    (let textVa = elfBaseVa + pageSize
    in
        let objectTextVa = textVa + linuxTrampolineLength
        in
            let codeLength = linuxTrampolineLength + Ashes.Byte.length(textBytes) + listLength(imports) * linuxImportStubLength
            in
                let dataFileOffset = alignUp(pageSize + codeLength)(pageSize)
                in
                    let dataVa = elfBaseVa + dataFileOffset
                    in
                        let layout = buildDynamicImportLayout(imports)(dataVa)
                        in
                            let gotVa = dataVa + layout.gotDataOffset
                            in
                                let rodataFileOffset = alignUp(dataFileOffset + Ashes.Byte.length(layout.bytes))(pageSize)
                                in
                                    let rodataVa = elfBaseVa + rodataFileOffset
                                    in
                                        match buildImportStubs(imports)(objectTextVa + Ashes.Byte.length(textBytes))(gotVa) with
                                            | (stubBytes, stubVas) ->
                                                let patchedTextBytes =
                                                    textBytes
                                                    |> applyTextPatches(textPatches)(stubVas)(objectTextVa)
                                                    |> applyDataPatches(dataPatches)(rodataVa)(objectTextVa)(objectTextVa)
                                                    |> Ashes.Byte.append(buildLinuxTrampoline(entrySymbol.symValue))
                                                in
                                                    let codeBytes = Ashes.Byte.append(patchedTextBytes)(stubBytes)
                                                    in
                                                        let totalLoadSize = pageSize + Ashes.Byte.length(codeBytes)
                                                        in
                                                            let entryPoint = textVa
                                                            in
                                                                let dynamicPlans =
                                                                    [
                                                                        ProgramHeaderPlan(
                                                                            phType = 1,
                                                                            phFlags = 5,
                                                                            phFileOffset = 0,
                                                                            phVirtualAddress = elfBaseVa,
                                                                            phSize = totalLoadSize,
                                                                            phAlignment = pageSize
                                                                        ),
                                                                        ProgramHeaderPlan(
                                                                            phType = 1,
                                                                            phFlags = 6,
                                                                            phFileOffset = dataFileOffset,
                                                                            phVirtualAddress = dataVa,
                                                                            phSize = Ashes.Byte.length(layout.bytes),
                                                                            phAlignment = pageSize
                                                                        ),
                                                                        ProgramHeaderPlan(
                                                                            phType = 3,
                                                                            phFlags = 4,
                                                                            phFileOffset = dataFileOffset + layout.interpDataOffset,
                                                                            phVirtualAddress = dataVa + layout.interpDataOffset,
                                                                            phSize = layout.interpByteCount,
                                                                            phAlignment = 1
                                                                        ),
                                                                        ProgramHeaderPlan(
                                                                            phType = 2,
                                                                            phFlags = 6,
                                                                            phFileOffset = dataFileOffset + layout.dynamicDataOffset,
                                                                            phVirtualAddress = dataVa + layout.dynamicDataOffset,
                                                                            phSize = layout.dynamicByteCount,
                                                                            phAlignment = 8
                                                                        )
                                                                    ]
                                                                in
                                                                    let plans =
                                                                        match rodataBytes with
                                                                            | None -> dynamicPlans
                                                                            | Some(rodata) ->
                                                                                appendList(dynamicPlans)(
                                                                                    [
                                                                                        ProgramHeaderPlan(
                                                                                            phType = 1,
                                                                                            phFlags = 4,
                                                                                            phFileOffset = rodataFileOffset,
                                                                                            phVirtualAddress = rodataVa,
                                                                                            phSize = Ashes.Byte.length(rodata),
                                                                                            phAlignment = pageSize
                                                                                        )
                                                                                    ]
                                                                                )
                                                                    in
                                                                        let headerAndCode =
                                                                            Ashes.Byte.append(buildHeaderPage(entryPoint)(plans))(codeBytes)
                                                                        in
                                                                            let paddedToDynamicData =
                                                                                Ashes.Byte.append(headerAndCode)(
                                                                                    Ashes.Byte.allocate(dataFileOffset - Ashes.Byte.length(headerAndCode))
                                                                                )
                                                                            in
                                                                                let headerCodeAndDynamicData = Ashes.Byte.append(paddedToDynamicData)(layout.bytes)
                                                                                in
                                                                                    match rodataBytes with
                                                                                        | None -> Ok(headerCodeAndDynamicData)
                                                                                        | Some(unpatchedRodata) ->
                                                                                            let paddedToRodata =
                                                                                                Ashes.Byte.append(headerCodeAndDynamicData)(
                                                                                                    Ashes.Byte.allocate(rodataFileOffset - Ashes.Byte.length(headerCodeAndDynamicData))
                                                                                                )
                                                                                            in
                                                                                                unpatchedRodata
                                                                                                |> applyDataPatches(rodataPatches)(rodataVa)(objectTextVa)(rodataVa)
                                                                                                |> Ashes.Byte.append(paddedToRodata)
                                                                                                |> Ok)

// Links a relocatable object (produced by `targetMachineEmitToMemoryBuffer(...)(objectFileType)`)
// into a runnable ELF64 executable for linux-x64. `entrySymbolName` must name a function defined
// in the object's `.text` section — the file's `e_entry` is set to that symbol's own address, not
// merely the start of `.text`.
//
// If `.text` carries no relocations, this is a fully static, non-PIE executable (a single `R+X`
// `PT_LOAD` segment covering the whole file). If it carries `R_X86_64_PLT32` relocations against
// symbols this linker recognizes (`linuxDynamicImportLibraries` — the narrow set
// `AshesCompiler.Backend.IrCodegen` can actually call today), it gains eager (non-lazy) dynamic
// linking: a `jmp`-through-GOT stub per import, a `.dynamic` section, and the ELF hash/`.dynstr`/
// `.dynsym`/`.rela.dyn` machinery the dynamic loader needs to resolve them. Any relocation this
// linker cannot resolve correctly is an `Error`, never a silently wrong link. No section headers
// are written either way — the kernel loader only consults program headers to run a binary.
let linkLinuxExecutable objectBytes entrySymbolName =
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
                                | None -> Error("linker: object has no .text section")
                                | Some((textSectionIndex, textSection)) ->
                                    match findSectionIndexByName(objectBytes)(shoff)(shentsize)(shnum)(shstrtabOffset)(".symtab")(0) with
                                        | None -> Error("linker: object has no symbol table")
                                        | Some((_, symtabSection)) ->
                                            let strtabSection = readSectionHeader(objectBytes)(shoff)(shentsize)(symtabSection.sectionLink)
                                            in
                                                let symbolCount = symtabSection.sectionSize / 24
                                                in
                                                    match findSymbolByName(objectBytes)(symtabSection.sectionOffset)(symbolCount)(strtabSection.sectionOffset)(
                                                        entrySymbolName
                                                    )(0) with
                                                        | None -> Error("linker: object does not define entry symbol '" + entrySymbolName + "'")
                                                        | Some(entrySymbol) ->
                                                            if entrySymbol.symSectionIndex != textSectionIndex
                                                            then Error("linker: entry symbol '" + entrySymbolName + "' is not defined in .text")
                                                            else
                                                                let rodataLayouts = collectRodataSectionLayouts(objectBytes)(shoff)(shentsize)(shnum)(shstrtabOffset)(0)(0)([])
                                                                in
                                                                    match collectTextPatches(objectBytes)(shoff)(shentsize)(shnum)(textSectionIndex)(symtabSection.sectionOffset)(
                                                                        strtabSection.sectionOffset
                                                                    )(rodataLayouts)(0)([])([]) with
                                                                        | Error(message) -> Error(message)
                                                                        | Ok(CollectedTextRelocations { functionPatches = functionPatches, dataPatches = dataPatches }) ->
                                                                            match collectRodataPatches(objectBytes)(shoff)(shentsize)(shnum)(textSectionIndex)(symtabSection.sectionOffset)(strtabSection.sectionOffset)(rodataLayouts)(0)([]) with
                                                                                | Error(message) -> Error(message)
                                                                                | Ok(rodataPatches) ->
                                                                                    let textBytes =
                                                                                        Ashes.Byte.copyRange(Ashes.Byte.allocate(textSection.sectionSize))(0)(objectBytes)(
                                                                                            textSection.sectionOffset
                                                                                        )(textSection.sectionSize)
                                                                                    in
                                                                                        let rodataBytes = buildRodataImage(objectBytes)(rodataLayouts)
                                                                                        in
                                                                                            match functionPatches with
                                                                                                | [] -> linkWithoutDynamicImports(textBytes)(entrySymbol)(dataPatches)(rodataPatches)(rodataBytes)
                                                                                                | _ ->
                                                                                                    let imports =
                                                                                                        collectLinuxDynamicImports(objectBytes)(symtabSection.sectionOffset)(symbolCount)(
                                                                                                            strtabSection.sectionOffset
                                                                                                        )
                                                                                                    in linkWithDynamicImports(objectBytes)(textBytes)(entrySymbol)(functionPatches)(dataPatches)(rodataPatches)(imports)(rodataBytes))
