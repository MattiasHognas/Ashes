// Manages source coordinates and maps AST/IR spans to source locations.
//
// Invariants:
// - All positions and offsets count UTF-8 bytes.
// - Runtime and memory-management instructions remain unlocated (line 0) to avoid false breakpoints.
// - Multi-file regions resolve to their respective file paths and relative coordinates.
// - Stitching glue outside valid module regions produces unlocated instructions.
// - A stitched project keeps every module's spans as offsets into that module's own file (the
//   stitcher combines syntax trees, never re-rendered text), so a span resolves through the
//   combined item index it belongs to: the item's module region names the file, and the file's own
//   line index turns the offset into the file's line and column. A span of an item outside every
//   region is stitching glue and stays unlocated.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
export (
    type SourceTextIndex(..),
    type ModuleRegion(..),
    type StitchedItemRegion(..),
    type SourceContext(..),
    value buildSourceTextIndex,
    value resolvePositionInIndex,
    value createSourceContext,
    value createMultiFileSourceContext,
    value createStitchedSourceContext,
    value resolveSourceLocation,
    value resolveOffsetLocation,
    value resolveItemSpanLocation,
    value isRuntimeMachinery,
    value annotateInstructionLocation,
    value tagInstruction,
    value tagItemInstruction,
)

type SourceTextIndex =
    | sourceText: Str
    | lineStarts: List(Int)
    deriving {Eq, Show}

type ModuleRegion =
    | regionFilePath: Str
    | startOffset: Int
    | endOffset: Int
    deriving {Eq, Show}

// The half-open range of combined top-level items one stitched module contributed, with the file
// whose offsets those items' spans use.
type StitchedItemRegion =
    | itemFilePath: Str
    | itemStart: Int
    | itemEnd: Int
    deriving {Eq, Show}

type SourceContext =
    | defaultFilePath: Str
    | mainSourceIndex: SourceTextIndex
    | moduleRegions: List(ModuleRegion)
    | moduleSourceIndexes: List(SourceTextIndex)
    | itemRegions: List(StitchedItemRegion)
    | itemSourceIndexes: List(SourceTextIndex)
    deriving {Eq, Show}

let recursive scanLineStarts (bytes: Bytes) (limit: Int) (i: Int) (acc: List(Int)) =
    if i >= limit
    then reverse(acc)
    else
        let b = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i))
        in
            if b == 10
            then scanLineStarts(bytes)(limit)(i + 1)(i + 1 :: acc)
            else
                if b == 13
                then
                // '\n' newline
                    // '\r' carriage return
                    if i + 1 < limit
                    then
                        let nextB = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i + 1))
                        in
                            if nextB == 10
                            then scanLineStarts(bytes)(limit)(i + 2)(i + 2 :: acc)
                            else scanLineStarts(bytes)(limit)(i + 1)(i + 1 :: acc)
                    else scanLineStarts(bytes)(limit)(i + 1)(i + 1 :: acc)
                else scanLineStarts(bytes)(limit)(i + 1)(acc)

let buildSourceTextIndex (source: Str) =
    (let bytes = Ashes.Byte.fromText(source)
    in
        let limit = Ashes.Byte.length(bytes)
        in
            let lineStarts = scanLineStarts(bytes)(limit)(0)([0])
            in SourceTextIndex(sourceText = source, lineStarts = lineStarts))

let recursive findLine (lineStarts: List(Int)) (targetOffset: Int) (currentLine: Int) (lastStart: Int) =
    match lineStarts with
        | [] -> (currentLine, lastStart)
        | head :: tail ->
            if head > targetOffset
            then (currentLine, lastStart)
            else findLine(tail)(targetOffset)(currentLine + 1)(head)

let resolvePositionInIndex (sourceIndex: SourceTextIndex) (offset: Int) =
    match sourceIndex with
        | SourceTextIndex(_sourceText, lineStarts) ->
            let normalizedOffset =
                if offset < 0
                then 0
                else offset
            in
                match lineStarts with
                    | [] -> (1, normalizedOffset + 1)
                    | _ :: tail ->
                        match findLine(tail)(normalizedOffset)(1)(0) with
                            | (line, lineStart) ->
                                let col = normalizedOffset - lineStart + 1
                                in (line, col)

let createSourceContext (filePath: Str) (source: Str) =
    (let index = buildSourceTextIndex(source)
    in
        SourceContext(
            defaultFilePath = filePath,
            mainSourceIndex = index,
            moduleRegions = [],
            moduleSourceIndexes = [],
            itemRegions = [],
            itemSourceIndexes = []
        ))

// The index lists are built by ordinary recursion, one region deep per module, rather than by an
// accumulator loop finished with `reverse`: stage 0 frees the record elements of an accumulator
// list that `reverse` moved into its result (the loop's exit drop finds the consumed cells unique
// and releases their records), so the indexes read back clobbered once later allocations reuse
// the memory. Region counts are small, so the recursion depth is not a concern.
let recursive buildModuleIndexes (regions: List(ModuleRegion)) (source: Str) =
    match regions with
        | [] -> []
        | ModuleRegion(_path, start, end) :: tail ->
            let bytes = Ashes.Byte.fromText(source)
            in
                let regionLen =
                    if end > start
                    then end - start
                    else 0
                in
                    let regionText = Ashes.Byte.subText(bytes)(start)(regionLen)
                    in buildSourceTextIndex(regionText) :: buildModuleIndexes(tail)(source)

let createMultiFileSourceContext (source: Str) (regions: List(ModuleRegion)) (defaultPath: Str) =
    (let mainIndex = buildSourceTextIndex(source)
    in
        let moduleIndexes = buildModuleIndexes(regions)(source)
        in
            SourceContext(
                defaultFilePath = defaultPath,
                mainSourceIndex = mainIndex,
                moduleRegions = regions,
                moduleSourceIndexes = moduleIndexes,
                itemRegions = [],
                itemSourceIndexes = []
            ))

let recursive lookupModuleSource (filePath: Str) (moduleSources: List((Str, Str))) =
    match moduleSources with
        | [] -> ""
        | (candidatePath, source) :: tail ->
            if candidatePath == filePath
            then source
            else lookupModuleSource(filePath)(tail)

// One line index per item region, built from that region's own file text (a region whose file
// text is missing indexes the empty text, so its spans resolve to line 1).
let recursive buildItemIndexes (regions: List(StitchedItemRegion)) (moduleSources: List((Str, Str))) =
    match regions with
        | [] -> []
        | StitchedItemRegion { itemFilePath = filePath } :: tail -> buildSourceTextIndex(lookupModuleSource(filePath)(moduleSources)) :: buildItemIndexes(tail)(moduleSources)

// A context for a stitched project: each module's file text keyed by its path, the item regions in
// combined order, and the entry file as the default path for spans resolved without an item.
let createStitchedSourceContext (moduleSources: List((Str, Str))) (regions: List(StitchedItemRegion)) (defaultPath: Str) =
    SourceContext(
        defaultFilePath = defaultPath,
        mainSourceIndex = buildSourceTextIndex(lookupModuleSource(defaultPath)(moduleSources)),
        moduleRegions = [],
        moduleSourceIndexes = [],
        itemRegions = regions,
        itemSourceIndexes = buildItemIndexes(regions)(moduleSources)
    )

let recursive resolveInRegions (regions: List(ModuleRegion)) (indexes: List(SourceTextIndex)) (offset: Int) =
    match (regions, indexes) with
        | ([], _) -> None
        | (_, []) -> None
        | (ModuleRegion(filePath, start, end) :: restReg, modIdx :: restIdx) ->
            if offset >= start
            then
                if offset < end
                then
                                // '\r\n' CRLF
                    let relOffset = offset - start
                    in
                        match resolvePositionInIndex(modIdx)(relOffset) with
                            | (line, col) ->
                                Some(
                                    IrSourceLocation(
                                        filePath = filePath,
                                        line = line,
                                        column = col
                                    )
                                )
                else resolveInRegions(restReg)(restIdx)(offset)
            else resolveInRegions(restReg)(restIdx)(offset)

let resolveOffsetLocation (context: SourceContext) (offset: Int) =
    match context with
        | SourceContext { defaultFilePath = defPath, mainSourceIndex = mainIdx, moduleRegions = [] } ->
            match resolvePositionInIndex(mainIdx)(offset) with
                | (line, col) ->
                    Some(
                        IrSourceLocation(
                            filePath = defPath,
                            line = line,
                            column = col
                        )
                    )
        | SourceContext { moduleRegions = regions, moduleSourceIndexes = indexes } -> resolveInRegions(regions)(indexes)(offset)

let resolveSourceLocation (context: SourceContext) (span: TextSpan) =
    match span with
        | TextSpan(0, 0) -> None
        | TextSpan(start, _end) -> resolveOffsetLocation(context)(start)

// The file and file-relative line index of the region holding a combined item, if any.
let recursive resolveItemRegion (regions: List(StitchedItemRegion)) (indexes: List(SourceTextIndex)) (itemIndex: Int) =
    match (regions, indexes) with
        | ([], _) -> None
        | (_, []) -> None
        | (StitchedItemRegion { itemFilePath = filePath, itemStart = start, itemEnd = end } :: restRegions, index :: restIndexes) ->
            if itemIndex >= start
            then
                if itemIndex < end
                then Some((filePath, index))
                else resolveItemRegion(restRegions)(restIndexes)(itemIndex)
            else resolveItemRegion(restRegions)(restIndexes)(itemIndex)

// A span of the combined item at itemIndex: in a stitched context the item's region names the
// file and the span's start is an offset into that file; an item outside every region is
// stitching glue and stays unlocated. Without item regions the span resolves by offset as before.
let resolveItemSpanLocation (context: SourceContext) (itemIndex: Int) (span: TextSpan) =
    match context with
        | SourceContext { itemRegions = [] } -> resolveSourceLocation(context)(span)
        | SourceContext { itemRegions = regions, itemSourceIndexes = indexes } ->
            match span with
                | TextSpan(0, 0) -> None
                | TextSpan(start, _end) ->
                    match resolveItemRegion(regions)(indexes)(itemIndex) with
                        | None -> None
                        | Some((filePath, index)) ->
                            match resolvePositionInIndex(index)(start) with
                                | (line, col) ->
                                    Some(
                                        IrSourceLocation(
                                            filePath = filePath,
                                            line = line,
                                            column = col
                                        )
                                    )

let isRuntimeMachinery (instruction: IrInstructionKind) =
    match instruction with
        | SaveArenaState(_, _, _) -> true
        | RestoreArenaState(_, _, _, _) -> true
        | ReclaimArenaChunks(_, _, _) -> true
        | CopyOutArena(_, _, _, _, _, _) -> true
        | CopyOutArenaToSpace(_, _, _) -> true
        | CleanupResource(_, _, _) -> true
        | DropReuse(_, _, _, _) -> true
        | RcDrop(_, _, _, _, _, _) -> true
        | RcDup(_, _, _, _) -> true
        | RcIsUnique(_, _) -> true
        | Borrow(_, _) -> true
        | _ -> false

let annotateInstructionLocation (instruction: IrInstruction) (location: Maybe(IrSourceLocation)) =
    match instruction with
        | IrInstruction(kind, _loc) -> IrInstruction(instruction = kind, location = location)

let tagInstruction (kind: IrInstructionKind) (currentSpan: Maybe(TextSpan)) (context: Maybe(SourceContext)) =
    if isRuntimeMachinery(kind)
    then IrInstruction(instruction = kind, location = None)
    else
        match (currentSpan, context) with
            | (Some(span), Some(ctx)) ->
                let loc = resolveSourceLocation(ctx)(span)
                in IrInstruction(instruction = kind, location = loc)
            | _ -> IrInstruction(instruction = kind, location = None)

// The item-aware form used by lowering: the span belongs to the combined item at itemIndex.
let tagItemInstruction (kind: IrInstructionKind) (currentSpan: Maybe(TextSpan)) (itemIndex: Int) (context: Maybe(SourceContext)) =
    if isRuntimeMachinery(kind)
    then IrInstruction(instruction = kind, location = None)
    else
        match (currentSpan, context) with
            | (Some(span), Some(ctx)) ->
                let loc = resolveItemSpanLocation(ctx)(itemIndex)(span)
                in IrInstruction(instruction = kind, location = loc)
            | _ -> IrInstruction(instruction = kind, location = None)
