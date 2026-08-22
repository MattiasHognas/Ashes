// Manages source coordinates and maps AST/IR spans to source locations.
//
// Invariants:
// - All positions and offsets count UTF-8 bytes.
// - Runtime and memory-management instructions remain unlocated (line 0) to avoid false breakpoints.
// - Multi-file regions resolve to their respective file paths and relative coordinates.
// - Stitching glue outside valid module regions produces unlocated instructions.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
export (
    type SourceTextIndex(..),
    type ModuleRegion(..),
    type SourceContext(..),
    value buildSourceTextIndex,
    value resolvePositionInIndex,
    value createSourceContext,
    value createMultiFileSourceContext,
    value resolveSourceLocation,
    value resolveOffsetLocation,
    value isRuntimeMachinery,
    value annotateInstructionLocation,
    value tagInstruction,
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

type SourceContext =
    | defaultFilePath: Str
    | mainSourceIndex: SourceTextIndex
    | moduleRegions: List(ModuleRegion)
    | moduleSourceIndexes: List(SourceTextIndex)
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
            moduleSourceIndexes = []
        ))

let recursive buildModuleIndexes (regions: List(ModuleRegion)) (source: Str) (acc: List(SourceTextIndex)) =
    match regions with
        | [] -> reverse(acc)
        | ModuleRegion(_path, start, end) :: tail ->
            let bytes = Ashes.Byte.fromText(source)
            in
                let regionLen =
                    if end > start
                    then end - start
                    else 0
                in
                    let regionText = Ashes.Byte.subText(bytes)(start)(regionLen)
                    in
                        let moduleIndex = buildSourceTextIndex(regionText)
                        in buildModuleIndexes(tail)(source)(moduleIndex :: acc)

let createMultiFileSourceContext (source: Str) (regions: List(ModuleRegion)) (defaultPath: Str) =
    (let mainIndex = buildSourceTextIndex(source)
    in
        let moduleIndexes = buildModuleIndexes(regions)(source)([])
        in
            SourceContext(
                defaultFilePath = defaultPath,
                mainSourceIndex = mainIndex,
                moduleRegions = regions,
                moduleSourceIndexes = moduleIndexes
            ))

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
        | SourceContext(defPath, mainIdx, [], _indexes) ->
            match resolvePositionInIndex(mainIdx)(offset) with
                | (line, col) ->
                    Some(
                        IrSourceLocation(
                            filePath = defPath,
                            line = line,
                            column = col
                        )
                    )
        | SourceContext(_defPath, _mainIdx, regions, indexes) -> resolveInRegions(regions)(indexes)(offset)

let resolveSourceLocation (context: SourceContext) (span: TextSpan) =
    match span with
        | TextSpan(0, 0) -> None
        | TextSpan(start, _end) -> resolveOffsetLocation(context)(start)

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
