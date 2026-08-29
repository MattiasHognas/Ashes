// Formats a whole source file: the written import header and the comments around the parsed body.
//
// Invariants:
// - Leading comment lines and import lines are preserved textually; imports are re-rendered canonically.
// - Standalone comment lines are anchored to the significant lines around them by token signature.
// - A comment whose anchors disappear falls back to the previous anchor, then to the top of the file.

import Ashes.Collection.HashMap.empty as mapEmpty
import Ashes.Collection.HashMap.get as mapGet
import Ashes.Collection.HashMap.set as mapSet
import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.filter as filterList
import Ashes.Collection.List.map as listMap
import Ashes.Collection.List.reverse as reverseList
import Ashes.Internal.deepCopy as deepCopy
import Ashes.Text
import AshesCompiler.Frontend.Token
import AshesCompiler.Frontend.Lexer
import AshesCompiler.Frontend.Parser
import AshesCompiler.Formatter.Formatter
export (
    type SourceFormatError(..),
    value splitSourceLines,
    value extractLeadingComments,
    value extractImports,
    value reinsertStandaloneCommentLines,
    value formatSource,
)

type SourceFormatError =
    | InvalidImportLine(Int, Str)
    | SourceParseFailure(List(DiagnosticEntry))
    deriving {Eq, Show}

type SignificantLine =
    | index: Int
    | signature: Str
    | occurrence: Int

type CommentInsertion =
    | text: Str
    | previousAnchor: Maybe((Str, Int))
    | nextAnchor: Maybe((Str, Int))

let byteAt bytes index =
    index
    |> Ashes.Byte.get(bytes)
    |> Ashes.Number.UInt.toInt

let stripCarriageReturn line =
    (let bytes = Ashes.Byte.fromText(line)
    in
        let count = Ashes.Byte.length(bytes)
        in
            let kept =
                if count == 0
                then 0
                else
                    if byteAt(bytes)(count - 1) == 13
                    then count - 1
                    else count
            in Ashes.Byte.subText(bytes)(0)(kept))

let recursive dropTrailingEmptyLine lines =
    match lines with
        | [] -> []
        | "" :: [] -> []
        | line :: rest -> line :: dropTrailingEmptyLine(rest)

let splitSourceLines text =
    if text == ""
    then []
    else
        "\n"
        |> Ashes.Text.split(text)
        |> dropTrailingEmptyLine
        |> listMap(stripCarriageReturn)

let endsWithNewline text =
    (let bytes = Ashes.Byte.fromText(text)
    in
        let count = Ashes.Byte.length(bytes)
        in
            if count == 0
            then false
            else byteAt(bytes)(count - 1) == 10)

let isStandaloneCommentLine line =
    Ashes.Text.startsWith(Ashes.Text.trimStart(line))("//")

let recursive signatureOfTokens tokens signature =
    match tokens with
        | [] -> signature
        | Token { kind = kind, text = text } :: rest ->
            match kind with
                | EOF -> signature
                | Bad -> signature
                | _ ->
                    let piece = tokenKindName(kind) + ":" + text
                    in
                        if signature == ""
                        then signatureOfTokens(rest)(piece)
                        else signatureOfTokens(rest)(signature + "|" + piece)

// A closing `in` frequently shares a physical line with the expression it introduces (`in h + g`)
// in one formatting pass and stands alone on its own line the next (`in` / `h + g`) — a purely
// stylistic choice the formatter makes based on wrapping, not a semantic difference. Dropping a
// leading, non-solitary `in` keeps the signature the same either way; without this, the two forms
// hash completely differently and a comment anchored to the "h + g" line can fail to match at
// all, falling back to the top of the file instead of landing near its original position.
let dropLeadingMergedIn tokens =
    match tokens with
        | Token { kind = In } :: second :: restTail -> second :: restTail
        | _ -> tokens

let lineSignature line =
    if Ashes.Text.trim(line) == ""
    then ""
    else
        match tokenize(line) with
            | LexerResult { tokens = tokens } ->
                signatureOfTokens(dropLeadingMergedIn(tokens))("")

let recursive collectSignificantLinesGo lines index counts acc =
    match lines with
        | [] -> reverseList(acc)
        | line :: rest ->
            if isStandaloneCommentLine(line)
            then collectSignificantLinesGo(rest)(index + 1)(counts)(acc)
            else
                let signature = lineSignature(line)
                in
                    if signature == ""
                    then collectSignificantLinesGo(rest)(index + 1)(counts)(acc)
                    else
                        let occurrence =
                            match mapGet(signature)(counts) with
                                | None -> 1
                                | Some(count) -> count + 1
                        in
                            collectSignificantLinesGo(
                                rest,
                                index + 1,
                                mapSet(signature)(occurrence)(counts),
                                SignificantLine(index = index, signature = signature, occurrence = occurrence) :: acc
                            )

let collectSignificantLines lines = collectSignificantLinesGo(lines)(0)(mapEmpty)([])

let anchorKey signature occurrence = signature + "#" + Ashes.Text.fromInt(occurrence)

let recursive buildAnchorIndexMap significantLines map =
    match significantLines with
        | [] -> map
        | SignificantLine { index = index, signature = signature, occurrence = occurrence } :: rest ->
            map
            |> mapSet(anchorKey(signature)(occurrence))(index)
            |> buildAnchorIndexMap(rest)

let findAnchorIndex anchor anchorIndices =
    match anchor with
        | (signature, occurrence) ->
            mapGet(anchorKey(signature)(occurrence))(anchorIndices)

let headAnchor significantLines =
    match significantLines with
        | [] -> None
        | SignificantLine { signature = signature, occurrence = occurrence } :: _ -> Some((signature, occurrence))

let recursive collectCommentInsertionsGo lines index remaining previous acc =
    match lines with
        | [] -> reverseList(acc)
        | line :: rest ->
            if isStandaloneCommentLine(line)
            then
                collectCommentInsertionsGo(
                    rest,
                    index + 1,
                    remaining,
                    previous,
                    CommentInsertion(text = deepCopy(line), previousAnchor = previous, nextAnchor = headAnchor(remaining)) :: acc
                )
            else
                match remaining with
                    | SignificantLine { index = significantIndex, signature = signature, occurrence = occurrence } :: restSignificant ->
                        if significantIndex == index
                        then collectCommentInsertionsGo(rest)(index + 1)(restSignificant)(Some((signature, occurrence)))(acc)
                        else collectCommentInsertionsGo(rest)(index + 1)(remaining)(previous)(acc)
                    | [] -> collectCommentInsertionsGo(rest)(index + 1)([])(previous)(acc)

let collectCommentInsertions lines =
    collectCommentInsertionsGo(lines)(0)(collectSignificantLines(lines))(None)([])

let resolveInsertionPosition insertion anchorIndices =
    match insertion with
        | CommentInsertion { previousAnchor = previousAnchor, nextAnchor = nextAnchor } ->
            let nextPosition =
                match nextAnchor with
                    | None -> None
                    | Some(anchor) -> findAnchorIndex(anchor)(anchorIndices)
            in
                match nextPosition with
                    | Some(position) -> position
                    | None ->
                        let previousPosition =
                            match previousAnchor with
                                | None -> None
                                | Some(anchor) -> findAnchorIndex(anchor)(anchorIndices)
                        in
                            match previousPosition with
                                | Some(position) -> position + 1
                                | None -> 0

let insertionText insertion =
    match insertion with
        | CommentInsertion { text = text } -> text

let insertionKey position ordinal = Ashes.Text.fromInt(position) + "#" + Ashes.Text.fromInt(ordinal)

let recursive groupInsertionsByPosition insertions anchorIndices counts texts =
    match insertions with
        | [] -> (counts, texts)
        | insertion :: rest ->
            let position = resolveInsertionPosition(insertion)(anchorIndices)
            in
                let positionKey = Ashes.Text.fromInt(position)
                in
                    let ordinal =
                        match mapGet(positionKey)(counts) with
                            | None -> 0
                            | Some(count) -> count
                    in
                        groupInsertionsByPosition(
                            rest,
                            anchorIndices,
                            mapSet(positionKey)(ordinal + 1)(counts),
                            mapSet(insertionKey(position)(ordinal))(insertionText(insertion))(texts)
                        )

let recursive prependInsertionsFrom position ordinal count texts acc =
    if ordinal >= count
    then acc
    else
        match mapGet(insertionKey(position)(ordinal))(texts) with
            | None -> prependInsertionsFrom(position)(ordinal + 1)(count)(texts)(acc)
            | Some(text) -> prependInsertionsFrom(position)(ordinal + 1)(count)(texts)(text :: acc)

let insertionsBefore position grouped acc =
    match grouped with
        | (counts, texts) ->
            match mapGet(Ashes.Text.fromInt(position))(counts) with
                | None -> acc
                | Some(count) -> prependInsertionsFrom(position)(0)(count)(texts)(acc)

let recursive mergeCommentLines formattedLines index grouped acc =
    match formattedLines with
        | [] ->
            acc
            |> insertionsBefore(index)(grouped)
            |> reverseList
        | line :: rest -> mergeCommentLines(rest)(index + 1)(grouped)(line :: insertionsBefore(index)(grouped)(acc))

let reinsertStandaloneCommentLines originalSource formattedSource lineEnding =
    (let insertions =
        originalSource
        |> splitSourceLines
        |> collectCommentInsertions
    in
        let formattedLines = splitSourceLines(formattedSource)
        in
            let anchorIndices =
                buildAnchorIndexMap(collectSignificantLines(formattedLines))(mapEmpty)
            in
                let grouped = groupInsertionsByPosition(insertions)(anchorIndices)(mapEmpty)(mapEmpty)
                in
                    let terminator =
                        if endsWithNewline(formattedSource)
                        then lineEnding
                        else ""
                    in
                        Ashes.Text.join(lineEnding)(mergeCommentLines(formattedLines)(0)(grouped)([])) + terminator)

let recursive extractLeadingCommentsGo lines acc =
    match lines with
        | [] -> (reverseList(acc), [])
        | line :: rest ->
            if Ashes.Text.startsWith(line)("//")
            then extractLeadingCommentsGo(rest)(line :: acc)
            else
                if Ashes.Text.trimStart(line) == ""
                then extractLeadingCommentsGo(rest)(line :: acc)
                else (reverseList(acc), lines)

let extractLeadingComments source =
    match extractLeadingCommentsGo(splitSourceLines(source))([]) with
        | (leadingComments, remainingLines) -> (leadingComments, Ashes.Text.join("\n")(remainingLines))

let isUpperByte value =
    if value >= 65
    then value <= 90
    else false

let isLowerByte value =
    if value >= 97
    then value <= 122
    else false

let isDigitByte value =
    if value >= 48
    then value <= 57
    else false

let isIdentifierTailByte value =
    if isUpperByte(value)
    then true
    else
        if isLowerByte(value)
        then true
        else
            if isDigitByte(value)
            then true
            else value == 95

let recursive allIdentifierTailFrom bytes index count =
    if index >= count
    then true
    else
        if index
        |> byteAt(bytes)
        |> isIdentifierTailByte
        then allIdentifierTailFrom(bytes)(index + 1)(count)
        else false

type ImportWordShape =
    | ModuleWord
    | SelectorWord
    | OtherWord

let importWordShape word =
    (let bytes = Ashes.Byte.fromText(word)
    in
        let count = Ashes.Byte.length(bytes)
        in
            if count == 0
            then OtherWord
            else
                if !allIdentifierTailFrom(bytes)(1)(count)
                then OtherWord
                else
                    let head = byteAt(bytes)(0)
                    in
                        if isUpperByte(head)
                        then ModuleWord
                        else
                            if isLowerByte(head)
                            then SelectorWord
                            else
                                if head == 95
                                then SelectorWord
                                else OtherWord)

let recursive isImportPathSegments segments seenModule =
    match segments with
        | [] -> false
        | last :: [] ->
            match importWordShape(last) with
                | ModuleWord -> true
                | SelectorWord -> seenModule
                | OtherWord -> false
        | segment :: rest ->
            match importWordShape(segment) with
                | ModuleWord -> isImportPathSegments(rest)(true)
                | _ -> false

let isImportPath path =
    isImportPathSegments(Ashes.Text.split(path)("."))(false)

let isImportAlias alias =
    (let bytes = Ashes.Byte.fromText(alias)
    in
        let count = Ashes.Byte.length(bytes)
        in
            if count == 0
            then false
            else
                if !allIdentifierTailFrom(bytes)(1)(count)
                then false
                else
                    let head = byteAt(bytes)(0)
                    in
                        if isUpperByte(head)
                        then true
                        else isLowerByte(head))

let recursive splitWordsOn separator words =
    match words with
        | [] -> []
        | word :: rest ->
            rest
            |> splitWordsOn(separator)
            |> appendList(Ashes.Text.split(word)(separator))

let importWords line =
    [line]
    |> splitWordsOn(" ")
    |> splitWordsOn("\t")
    |> filterList(given (word) -> word != "")

let renderImportLine line =
    match importWords(line) with
        | "import" :: path :: [] ->
            if isImportPath(path)
            then Some("import " + path)
            else None
        | "import" :: path :: "as" :: alias :: [] ->
            if isImportPath(path)
            then
                if isImportAlias(alias)
                then Some("import " + path + " as " + alias)
                else None
            else None
        | _ -> None

let recursive extractImportsGo lines lineIndex imports kept =
    match lines with
        | [] ->
            Ok((reverseList(imports), kept
            |> reverseList
            |> Ashes.Text.join("\n")))
        | line :: rest ->
            match renderImportLine(line) with
                | Some(rendered) -> extractImportsGo(rest)(lineIndex + 1)(rendered :: imports)(kept)
                | None ->
                    if Ashes.Text.startsWith(Ashes.Text.trimStart(line))("import ")
                    then
                        line
                        |> InvalidImportLine(lineIndex)
                        |> Error
                    else extractImportsGo(rest)(lineIndex + 1)(imports)(line :: kept)

let extractImports source =
    extractImportsGo(splitSourceLines(source))(1)([])([])

let prependLines lines body =
    (let separator =
        match lines with
            | [] -> ""
            | _ -> "\n"
    in Ashes.Text.join("\n")(lines) + separator + body)

// The anchor signature+occurrence pair is computed independently on the pre-format source and the
// post-format output; if formatting changes how a comment's neighboring line wraps (or how many
// other lines elsewhere in the file share its signature), the two computations can disagree and
// land a comment a line or two off. Re-running the same parse/format/reinsert pass on that
// slightly-off output almost always converges immediately, since from the second pass on the
// underlying code is already in its canonical, stable form — only the comment placement was ever
// unsettled. Bounds `formatBodyToFixedPoint`'s iteration.
let maxFixedPointPasses = 5

// Repeatedly parses, formats, and reinserts comments into `sourceWithoutImports` until the result
// stops changing, capped at `maxFixedPointPasses` — so a single `formatSource` call already
// returns fully idempotent output instead of requiring the caller to re-run formatting manually.
let recursive formatBodyToFixedPoint sourceWithoutImports passesRemaining =
    match parseProgram(sourceWithoutImports) with
        | ProgramParseResult { program = program, diagnostics = [] } ->
            let formattedBody =
                reinsertStandaloneCommentLines(sourceWithoutImports)(formatProgram(program))("\n")
            in
                if formattedBody == sourceWithoutImports
                then Ok(formattedBody)
                else
                    if passesRemaining <= 1
                    then Ok(formattedBody)
                    else formatBodyToFixedPoint(formattedBody)(passesRemaining - 1)
        | ProgramParseResult { diagnostics = diagnostics } -> Error(SourceParseFailure(diagnostics))

let formatSource source =
    match extractLeadingComments(source) with
        | (leadingComments, sourceWithoutComments) ->
            match extractImports(sourceWithoutComments) with
                | Error(error) -> Error(error)
                | Ok((imports, sourceWithoutImports)) ->
                    match formatBodyToFixedPoint(sourceWithoutImports)(maxFixedPointPasses) with
                        | Error(error) -> Error(error)
                        | Ok(formattedBody) ->
                            formattedBody
                            |> prependLines(imports)
                            |> prependLines(leadingComments)
                            |> Ok
