// Reads the leading import region without parsing the rest of a module.
//
// Invariants:
// - Only imports before the first non-import top-level form belong to the header.
// - Written import text and source order are retained for deterministic diagnostics.
// - Body offsets and import spans are absolute UTF-8 byte offsets.

import Ashes.Collection.List.reverse as reverseList
import Ashes.Internal.deepCopy as deepCopy
import Ashes.Text
export (
    type ImportHeaderEntry(..),
    type ImportHeaderError(..),
    type ParsedImportHeader(..),
    value parseImportHeader,
)

type ImportHeaderEntry =
    | modulePath: Str
    | selector: Maybe(Str)
    | alias: Maybe(Str)
    | sourceLine: Int
    | written: Str
    deriving {Eq, Show}

type ImportHeaderError =
    | InvalidImportSyntax(Int, Str)
    | ReservedImportAlias(Int, Str)
    | DuplicateImportAlias(Int, Str, Str)
    | ConflictingImportSelector(Int, Str)
    deriving {Eq, Show}

type ParsedImportHeader =
    | imports: List(ImportHeaderEntry)
    | sourceWithoutImports: Str
    | bodyStartByteOffset: Int
    deriving {Eq, Show}

let isAsciiUpper text =
    match text with
        | "A" -> true
        | "B" -> true
        | "C" -> true
        | "D" -> true
        | "E" -> true
        | "F" -> true
        | "G" -> true
        | "H" -> true
        | "I" -> true
        | "J" -> true
        | "K" -> true
        | "L" -> true
        | "M" -> true
        | "N" -> true
        | "O" -> true
        | "P" -> true
        | "Q" -> true
        | "R" -> true
        | "S" -> true
        | "T" -> true
        | "U" -> true
        | "V" -> true
        | "W" -> true
        | "X" -> true
        | "Y" -> true
        | "Z" -> true
        | _ -> false

let isAsciiLower text =
    match text with
        | "a" -> true
        | "b" -> true
        | "c" -> true
        | "d" -> true
        | "e" -> true
        | "f" -> true
        | "g" -> true
        | "h" -> true
        | "i" -> true
        | "j" -> true
        | "k" -> true
        | "l" -> true
        | "m" -> true
        | "n" -> true
        | "o" -> true
        | "p" -> true
        | "q" -> true
        | "r" -> true
        | "s" -> true
        | "t" -> true
        | "u" -> true
        | "v" -> true
        | "w" -> true
        | "x" -> true
        | "y" -> true
        | "z" -> true
        | _ -> false

let isAsciiDigit text =
    match text with
        | "0" -> true
        | "1" -> true
        | "2" -> true
        | "3" -> true
        | "4" -> true
        | "5" -> true
        | "6" -> true
        | "7" -> true
        | "8" -> true
        | "9" -> true
        | _ -> false

let isIdentifierTailCharacter text =
    if isAsciiUpper(text)
    then true
    else
        if isAsciiLower(text)
        then true
        else
            if isAsciiDigit(text)
            then true
            else text == "_"

let recursive allIdentifierTail text =
    match Ashes.Text.unconsText(text) with
        | None -> true
        | Some((head, tail)) ->
            if isIdentifierTailCharacter(head)
            then allIdentifierTail(tail)
            else false

let isModuleSegment text =
    match Ashes.Text.unconsText(text) with
        | Some((head, tail)) ->
            if isAsciiUpper(head)
            then allIdentifierTail(tail)
            else false
        | None -> false

let isSelectorSegment text =
    match Ashes.Text.unconsText(text) with
        | Some((head, tail)) ->
            if isAsciiLower(head)
            then allIdentifierTail(tail)
            else
                if head == "_"
                then allIdentifierTail(tail)
                else false
        | None -> false

let isAlias text =
    match Ashes.Text.unconsText(text) with
        | Some((head, tail)) ->
            if isAsciiUpper(head)
            then allIdentifierTail(tail)
            else
                if isAsciiLower(head)
                then allIdentifierTail(tail)
                else false
        | None -> false

let isReservedAlias text =
    match text with
        | "let" -> true
        | "recursive" -> true
        | "in" -> true
        | "if" -> true
        | "then" -> true
        | "else" -> true
        | "match" -> true
        | "with" -> true
        | "given" -> true
        | "true" -> true
        | "false" -> true
        | "type" -> true
        | "await" -> true
        | "external" -> true
        | "capability" -> true
        | "needs" -> true
        | "perform" -> true
        | "handle" -> true
        | "trait" -> true
        | "implement" -> true
        | "requires" -> true
        | _ -> false

let recursive importWordsGo current reversedWords text =
    match Ashes.Text.unconsText(text) with
        | None ->
            if current == ""
            then reverseList(reversedWords)
            else reverseList(current :: reversedWords)
        | Some((head, tail)) ->
            if head == " "
            then
                if current == ""
                then importWordsGo("")(reversedWords)(tail)
                else importWordsGo("")(current :: reversedWords)(tail)
            else
                if head == "\t"
                then
                    if current == ""
                    then importWordsGo("")(reversedWords)(tail)
                    else importWordsGo("")(current :: reversedWords)(tail)
                else importWordsGo(current + head)(reversedWords)(tail)

let importWords text = importWordsGo("")([])(text)

let appendModuleSegment prefix segment =
    if prefix == ""
    then deepCopy(segment)
    else prefix + "." + segment

let recursive parseImportSegments prefix segments =
    match segments with
        | [] -> None
        | last :: [] ->
            if isSelectorSegment(last)
            then
                if prefix == ""
                then None
                else
                    Some((deepCopy(prefix), last
                    |> deepCopy
                    |> Some))
            else
                if isModuleSegment(last)
                then Some((appendModuleSegment(prefix)(last), None))
                else None
        | segment :: rest ->
            if isModuleSegment(segment)
            then
                parseImportSegments(appendModuleSegment(prefix)(segment))(rest)
            else None

let parseImportPath path =
    "."
    |> Ashes.Text.split(path)
    |> parseImportSegments("")

let recursive findModuleAlias name imports =
    match imports with
        | [] -> None
        | ImportHeaderEntry { modulePath = modulePath, selector = None, alias = Some(candidate), sourceLine = _sourceLine, written = _written } :: rest ->
            if candidate == name
            then
                modulePath
                |> deepCopy
                |> Some
            else findModuleAlias(name)(rest)
        | _entry :: rest -> findModuleAlias(name)(rest)

let recursive hasSelectorConflict localName modulePath exportName imports =
    match imports with
        | [] -> false
        | ImportHeaderEntry { modulePath = existingModule, selector = Some(existingExport), alias = Some(existingLocal), sourceLine = _sourceLine, written = _written } :: rest ->
            if existingLocal == localName
            then
                if existingModule == modulePath
                then existingExport != exportName
                else true
            else hasSelectorConflict(localName)(modulePath)(exportName)(rest)
        | ImportHeaderEntry { modulePath = existingModule, selector = Some(existingExport), alias = None, sourceLine = _sourceLine, written = _written } :: rest ->
            if existingExport == localName
            then
                if existingModule == modulePath
                then existingExport != exportName
                else true
            else hasSelectorConflict(localName)(modulePath)(exportName)(rest)
        | _entry :: rest -> hasSelectorConflict(localName)(modulePath)(exportName)(rest)

let validateImportAlias sourceLine alias =
    match alias with
        | None -> Ok(Unit)
        | Some(name) ->
            if isAlias(name)
            then
                if isReservedAlias(name)
                then
                    name
                    |> ReservedImportAlias(sourceLine)
                    |> Error
                else Ok(Unit)
            else
                name
                |> InvalidImportSyntax(sourceLine)
                |> Error

let copyOptionalText value =
    match value with
        | None -> None
        | Some(text) ->
            text
            |> deepCopy
            |> Some

let selectorEntry written sourceLine modulePath exportName alias =
    ImportHeaderEntry(modulePath = deepCopy(modulePath), selector = exportName
    |> deepCopy
    |> Some, alias = copyOptionalText(alias), sourceLine = sourceLine, written = deepCopy(written))

let addSelectorImport written sourceLine modulePath exportName alias imports localName =
    match validateImportAlias(sourceLine)(localName
    |> deepCopy
    |> Some) with
        | Error(error) -> Error(error)
        | Ok(_) ->
            if hasSelectorConflict(localName)(modulePath)(exportName)(imports)
            then
                localName
                |> ConflictingImportSelector(sourceLine)
                |> Error
            else
                alias
                |> selectorEntry(written)(sourceLine)(modulePath)(exportName)
                |> Ok

let addSelectorImportWithAlias written sourceLine modulePath exportName alias imports =
    match alias with
        | Some(localName) ->
            addSelectorImport(written)(sourceLine)(modulePath)(exportName)(localName
            |> deepCopy
            |> Some)(imports)(localName)
        | None -> addSelectorImport(written)(sourceLine)(modulePath)(exportName)(None)(imports)(exportName)

let addModuleImport written sourceLine modulePath alias imports =
    match alias with
        | None ->
            Ok(
                ImportHeaderEntry(modulePath = deepCopy(
                    modulePath
                ), selector = None, alias = None, sourceLine = sourceLine, written = deepCopy(written))
            )
        | Some(name) ->
            match validateImportAlias(sourceLine)(name
            |> deepCopy
            |> Some) with
                | Error(error) -> Error(error)
                | Ok(_) ->
                    match findModuleAlias(name)(imports) with
                        | Some(existingModule) ->
                            existingModule
                            |> DuplicateImportAlias(sourceLine)(name)
                            |> Error
                        | None ->
                            Ok(ImportHeaderEntry(modulePath = deepCopy(modulePath), selector = None, alias = name
                            |> deepCopy
                            |> Some, sourceLine = sourceLine, written = deepCopy(written)))

let addParsedImport written sourceLine path alias imports =
    match parseImportPath(path) with
        | None ->
            written
            |> InvalidImportSyntax(sourceLine)
            |> Error
        | Some((modulePath, Some(exportName))) ->
            addSelectorImportWithAlias(
                written,
                sourceLine,
                modulePath,
                exportName,
                alias,
                imports
            )
        | Some((modulePath, None)) -> addModuleImport(written)(sourceLine)(modulePath)(alias)(imports)

let parseImportLine written sourceLine imports =
    match written
    |> Ashes.Text.trim
    |> importWords with
        | "import" :: path :: [] -> addParsedImport(written)(sourceLine)(path)(None)(imports)
        | "import" :: path :: "as" :: alias :: [] -> addParsedImport(written)(sourceLine)(path)(Some(alias))(imports)
        | _ ->
            written
            |> InvalidImportSyntax(sourceLine)
            |> Error

let headerByteAt (bytes: Bytes) (position: Int) =
    position
    |> Ashes.Byte.get(bytes)
    |> Ashes.Number.UInt.toInt

let recursive headerLineEnd (bytes: Bytes) (byteCount: Int) (position: Int) =
    if position >= byteCount
    then byteCount
    else
        if headerByteAt(bytes)(position) == 10
        then position
        else headerLineEnd(bytes)(byteCount)(position + 1)

let recursive headerContentStart (bytes: Bytes) (lineEnd: Int) (position: Int) =
    if position >= lineEnd
    then lineEnd
    else
        if position
        |> headerByteAt(bytes)
        |> Ashes.Text.isWhiteSpaceByte
        then headerContentStart(bytes)(lineEnd)(position + 1)
        else position

let headerLineIsComment (bytes: Bytes) (lineEnd: Int) (contentStart: Int) =
    if contentStart + 2 > lineEnd
    then false
    else
        if headerByteAt(bytes)(contentStart) == 47
        then headerByteAt(bytes)(contentStart + 1) == 47
        else false

let headerLineIsImport (bytes: Bytes) (lineEnd: Int) (contentStart: Int) =
    if contentStart + 6 > lineEnd
    then false
    else
        if Ashes.Byte.subText(bytes)(contentStart)(6) == "import"
        then
            if contentStart + 6 == lineEnd
            then true
            else
                let following = headerByteAt(bytes)(contentStart + 6)
                in
                    if following == 32
                    then true
                    else following == 9
        else false

let finishHeader importsReversed outputReversed bodyStartByteOffset =
    ParsedImportHeader(imports = reverseList(importsReversed), sourceWithoutImports = outputReversed
    |> reverseList
    |> Ashes.Text.join("\n"), bodyStartByteOffset = bodyStartByteOffset)

let recursive parseHeaderLines (bytes: Bytes) (byteCount: Int) (lineStart: Int) (sourceLine: Int) importsReversed outputReversed =
    if lineStart > byteCount
    then
        byteCount
        |> finishHeader(importsReversed)(outputReversed)
        |> Ok
    else
        let lineEnd = headerLineEnd(bytes)(byteCount)(lineStart)
        in
            let contentStart = headerContentStart(bytes)(lineEnd)(lineStart)
            in
                let isHeaderLine =
                    if contentStart >= lineEnd
                    then true
                    else headerLineIsComment(bytes)(lineEnd)(contentStart)
                in
                    if isHeaderLine
                    then
                        parseHeaderLines(
                            bytes,
                            byteCount,
                            lineEnd + 1,
                            sourceLine + 1,
                            importsReversed,
                            Ashes.Byte.subText(bytes)(lineStart)(lineEnd - lineStart) :: outputReversed
                        )
                    else
                        if headerLineIsImport(bytes)(lineEnd)(contentStart)
                        then
                            match parseImportLine(Ashes.Byte.subText(bytes)(lineStart)(lineEnd - lineStart))(sourceLine)(importsReversed) with
                                | Error(error) -> Error(error)
                                | Ok(entry) ->
                                    parseHeaderLines(
                                        bytes,
                                        byteCount,
                                        lineEnd + 1,
                                        sourceLine + 1,
                                        entry :: importsReversed,
                                        "" :: outputReversed
                                    )
                        else
                            lineStart
                            |> finishHeader(importsReversed)(Ashes.Byte.subText(bytes)(lineStart)(byteCount - lineStart) :: outputReversed)
                            |> Ok

let parseImportHeader source =
    (let bytes = Ashes.Byte.fromText(source)
    in
        parseHeaderLines(bytes)(Ashes.Byte.length(bytes))(0)(1)([])([]))
