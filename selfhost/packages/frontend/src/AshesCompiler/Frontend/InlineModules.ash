// Lifts inline module blocks into ordinary synthetic module sources.
//
// Invariants:
// - A block is defined by indentation past its `module Name =` header.
// - Nested modules are emitted before their parent so downstream planning can use dependency order.
// - Same-scope qualifiers are composed without rewriting strings or already-qualified paths.
// - Inline modules reject imports, externals, trailing expressions, duplicate names, and `Ashes`.

import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
export (
    type InlineModuleError(..),
    type InlineModuleInfo(..),
    type InlineModuleExpansion(..),
    value containsInlineModule,
    value expandInlineModules,
)

type InlineModuleError =
    | InlineModuleImport(Str)
    | InlineModuleExternal(Str)
    | InlineModuleTrailingExpression(Str)
    | ReservedInlineModule(Str)
    | DuplicateInlineModule(Str, Str)
    deriving {Eq, Show}

type InlineModuleInfo =
    | name: Str
    | source: Str
    deriving {Eq, Show}

type InlineModuleExpansion =
    | source: Str
    | modules: List(InlineModuleInfo)
    deriving {Eq, Show}

type InlineModuleHeader =
    | name: Str
    | indent: Int

type DirectInlineModule =
    | name: Str
    | body: List(Str)

type InlineModuleCollection =
    | names: List(Str)
    | outer: List(Str)
    | modules: List(DirectInlineModule)

type InlineModuleBody =
    | lines: List(Str)
    | remaining: List(Str)

let isHorizontalWhitespace character =
    match character with
        | " " -> true
        | "\t" -> true
        | _ -> false

let asciiCode character =
    character
    |> Ashes.Byte.fromText
    |> (given (bytes) -> Ashes.Byte.get(bytes)(0))
    |> Ashes.Number.UInt.toInt

let isAsciiLower character =
    (let code = asciiCode(character)
    in
        if code < 97
        then false
        else code <= 122)

let isAsciiUpper character =
    (let code = asciiCode(character)
    in
        if code < 65
        then false
        else code <= 90)

let isAsciiDigit character =
    (let code = asciiCode(character)
    in
        if code < 48
        then false
        else code <= 57)

let isNameCharacter character =
    match (isAsciiUpper(character), isAsciiLower(character), isAsciiDigit(character), character == "_") with
        | (false, false, false, false) -> false
        | _ -> true

let isIdentifierStart character =
    match (isAsciiUpper(character), isAsciiLower(character), character == "_") with
        | (false, false, false) -> false
        | _ -> true

// Every `"\r\n"` line end becomes `"\n"`; a lone `"\r"` stays. One split and one join walk the
// whole source in linear time and constant stack, where a character-by-character rebuild
// nested one frame per character and copied the remaining text at every level, so a source
// the size of a compiler module overflowed the stack before it was ever parsed.
let normalizeCrLf source =
    "\r\n"
    |> Ashes.Text.split(source)
    |> Ashes.Text.join("\n")

let recursive leadingWhitespaceWidthFrom line width =
    match Ashes.Text.unconsText(line) with
        | Some((head, tail)) ->
            if isHorizontalWhitespace(head)
            then leadingWhitespaceWidthFrom(tail)(width + 1)
            else width
        | None -> width

let leadingWhitespaceWidth line = leadingWhitespaceWidthFrom(line)(0)

let recursive dropHorizontalWhitespace text =
    match Ashes.Text.unconsText(text) with
        | Some((head, tail)) ->
            if isHorizontalWhitespace(head)
            then dropHorizontalWhitespace(tail)
            else text
        | None -> ""

let recursive takeName text name =
    match Ashes.Text.unconsText(text) with
        | Some((head, tail)) ->
            if isNameCharacter(head)
            then takeName(tail)(name + head)
            else (name, text)
        | None -> (name, "")

let finishHeader indent text =
    match Ashes.Text.unconsText(text) with
        | Some((first, _tail)) ->
            if isAsciiUpper(first)
            then
                match takeName(text)("") with
                    | (name, afterName) ->
                        let afterSpacing = dropHorizontalWhitespace(afterName)
                        in
                            if Ashes.Text.startsWith(afterSpacing)("=")
                            then
                                let suffix =
                                    1
                                    |> Ashes.Text.drop(afterSpacing)
                                    |> dropHorizontalWhitespace
                                in
                                    if suffix == ""
                                    then Some(InlineModuleHeader(name = name, indent = indent))
                                    else
                                        if Ashes.Text.startsWith(suffix)("//")
                                        then Some(InlineModuleHeader(name = name, indent = indent))
                                        else None
                            else None
            else None
        | None -> None

let parseInlineModuleHeader line =
    (let indent = leadingWhitespaceWidth(line)
    in
        let afterIndent = Ashes.Text.drop(line)(indent)
        in
            if Ashes.Text.startsWith(afterIndent)("module")
            then
                let afterKeyword = Ashes.Text.drop(afterIndent)(6)
                in
                    match Ashes.Text.unconsText(afterKeyword) with
                        | Some((separator, _tail)) ->
                            if isHorizontalWhitespace(separator)
                            then
                                afterKeyword
                                |> dropHorizontalWhitespace
                                |> finishHeader(indent)
                            else None
                        | None -> None
            else None)

let recursive containsHeader lines =
    match lines with
        | [] -> false
        | line :: rest ->
            match parseInlineModuleHeader(line) with
                | Some(_) -> true
                | None -> containsHeader(rest)

let containsInlineModule source =
    source
    |> normalizeCrLf
    |> (given (normalized) -> Ashes.Text.split(normalized)("\n"))
    |> containsHeader

let recursive containsName name names =
    match names with
        | [] -> false
        | candidate :: rest ->
            if candidate == name
            then true
            else containsName(name)(rest)

let recursive collectBody headerIndent lines reversed =
    match lines with
        | [] -> InlineModuleBody(lines = reverseList(reversed), remaining = [])
        | line :: rest ->
            if Ashes.Text.trim(line) == ""
            then collectBody(headerIndent)(rest)(line :: reversed)
            else
                if leadingWhitespaceWidth(line) > headerIndent
                then collectBody(headerIndent)(rest)(line :: reversed)
                else InlineModuleBody(lines = reverseList(reversed), remaining = lines)

// One module header and its body taken off the lines: the name and the module join the
// collected lists, and the lines after the body remain.
let collectInlineModule scope (header: InlineModuleHeader) remaining names modules =
    if containsName(header.name)(names)
    then
        header.name
        |> DuplicateInlineModule(scope)
        |> Error
    else
        let composedName =
            if scope == ""
            then header.name
            else scope + "." + header.name
        in
            if header.name == "Ashes"
            then Error(ReservedInlineModule(composedName))
            else
                match collectBody(header.indent)(remaining)([]) with
                    | InlineModuleBody { lines = body, remaining = afterBody } -> Ok((header.name :: names, DirectInlineModule(name = header.name, body = body) :: modules, afterBody))

// The collected names, outer lines, and modules travel as three list parameters, each grown by
// one cell at a time. A record accumulator holding the three lists was rebuilt every iteration
// and its lists copied out of the arena at every back edge, so a source of n lines cost n^2
// memory before it was ever parsed.
let recursive collectInlineModules scope lines names outer modules =
    match lines with
        | [] -> Ok(InlineModuleCollection(names = names, outer = outer, modules = modules))
        | line :: rest ->
            match parseInlineModuleHeader(line) with
                | None -> collectInlineModules(scope)(rest)(names)(line :: outer)(modules)
                | Some(header) ->
                    match collectInlineModule(scope)(header)(rest)(names)(modules) with
                        | Error(error) -> Error(error)
                        | Ok((nextNames, nextModules, remaining)) -> collectInlineModules(scope)(remaining)(nextNames)(outer)(nextModules)

let recursive minimumIndent lines current =
    match lines with
        | [] -> current
        | line :: rest ->
            if Ashes.Text.trim(line) == ""
            then minimumIndent(rest)(current)
            else
                let width = leadingWhitespaceWidth(line)
                in
                    match current with
                        | None -> minimumIndent(rest)(Some(width))
                        | Some(existing) ->
                            if width < existing
                            then minimumIndent(rest)(Some(width))
                            else minimumIndent(rest)(current)

let recursive dedentLines lines indent =
    match lines with
        | [] -> []
        | line :: rest ->
            let dedented =
                if Ashes.Text.length(line) >= indent
                then Ashes.Text.drop(line)(indent)
                else line
            in dedented :: dedentLines(rest)(indent)

let dedent lines =
    match minimumIndent(lines)(None) with
        | None -> Ashes.Text.join("\n")(lines)
        | Some(0) -> Ashes.Text.join("\n")(lines)
        | Some(indent) ->
            indent
            |> dedentLines(lines)
            |> Ashes.Text.join("\n")

let byteAt bytes index =
    index
    |> Ashes.Byte.get(bytes)
    |> Ashes.Number.UInt.toInt

let isNameByte code =
    match (code >= 65 && code <= 90, code >= 97 && code <= 122, code >= 48 && code <= 57, code == 95) with
        | (false, false, false, false) -> false
        | _ -> true

let isIdentifierStartByte code =
    match (code >= 65 && code <= 90, code >= 97 && code <= 122, code == 95) with
        | (false, false, false) -> false
        | _ -> true

// A qualifier may start at `index` unless the byte before it continues a name or is another
// `.` (a longer qualified path). A byte of a multi-byte character is neither.
let previousAllowsQualifierAt bytes index =
    if index == 0
    then true
    else
        let code = byteAt(bytes)(index - 1)
        in
            if isNameByte(code)
            then false
            else code != 46

let recursive bytesMatchAt bytes length index (needle: Bytes) needleIndex needleLength =
    if needleIndex >= needleLength
    then true
    else
        if index >= length
        then false
        else
            if byteAt(bytes)(index) == byteAt(needle)(needleIndex)
            then bytesMatchAt(bytes)(length)(index + 1)(needle)(needleIndex + 1)(needleLength)
            else false

// `name + "."` sits at `index` and an identifier start follows it.
let childMatchesQualifierAt bytes length index name =
    (let prefix = Ashes.Byte.fromText(name + ".")
    in
        let prefixLength = Ashes.Byte.length(prefix)
        in
            if bytesMatchAt(bytes)(length)(index)(prefix)(0)(prefixLength)
            then
                if index + prefixLength < length
                then
                    index + prefixLength
                    |> byteAt(bytes)
                    |> isIdentifierStartByte
                else false
            else false)

let recursive findQualifiedChildAt bytes length index childNames =
    match childNames with
        | [] -> None
        | name :: rest ->
            if childMatchesQualifierAt(bytes)(length)(index)(name)
            then Some(name)
            else findQualifiedChildAt(bytes)(length)(index)(rest)

// The source is walked by byte index and copied out in segments: the text between two
// rewritten qualifiers is one `subText`, so a module with no qualifier to rewrite costs one copy
// of its source. A character-by-character walk took a fresh tail of the text at every step and
// nested one frame per character, which cost quadratic memory on a module-sized source.
let recursive rewriteQualifierBytes scope childNames bytes length index segmentStart inString chunks =
    if index >= length
    then
        Ashes.Byte.subText(bytes)(segmentStart)(length - segmentStart) :: chunks
        |> reverseList
        |> Ashes.Text.join("")
    else
        let code = byteAt(bytes)(index)
        in
            if inString
            then
                if code == 92
                then rewriteQualifierBytes(scope)(childNames)(bytes)(length)(index + 2)(segmentStart)(true)(chunks)
                else rewriteQualifierBytes(scope)(childNames)(bytes)(length)(index + 1)(segmentStart)(code != 34)(chunks)
            else
                if code == 34
                then rewriteQualifierBytes(scope)(childNames)(bytes)(length)(index + 1)(segmentStart)(true)(chunks)
                else
                    if previousAllowsQualifierAt(bytes)(index)
                    then
                        match findQualifiedChildAt(bytes)(length)(index)(childNames) with
                            | Some(name) ->
                                let consumed = Ashes.Text.byteLength(name) + 1
                                in rewriteQualifierBytes(scope)(childNames)(bytes)(length)(index + consumed)(index + consumed)(false)(scope + "." + name + "." :: Ashes.Byte.subText(bytes)(segmentStart)(index - segmentStart) :: chunks)
                            | None -> rewriteQualifierBytes(scope)(childNames)(bytes)(length)(index + 1)(segmentStart)(false)(chunks)
                    else rewriteQualifierBytes(scope)(childNames)(bytes)(length)(index + 1)(segmentStart)(false)(chunks)

let rewriteInlineQualifiers scope childNames source =
    if scope == ""
    then source
    else
        let bytes = Ashes.Byte.fromText(source)
        in
            rewriteQualifierBytes(scope)(childNames)(bytes)(Ashes.Byte.length(bytes))(0)(0)(false)([])

let recursive containsImportLine lines =
    match lines with
        | [] -> false
        | line :: rest ->
            let trimmed = Ashes.Text.trim(line)
            in
                if Ashes.Text.startsWith(trimmed)("import ")
                then true
                else
                    if Ashes.Text.startsWith(trimmed)("import\t")
                    then true
                    else containsImportLine(rest)

let recursive unspanTopLevel item =
    match item with
        | TopLevelAt(_span, inner) -> unspanTopLevel(inner)
        | _ -> item

let recursive containsExternal items =
    match items with
        | [] -> false
        | item :: rest ->
            match unspanTopLevel(item) with
                | TopLevelExternal(_) -> true
                | _ -> containsExternal(rest)

let validateParsedBody moduleName (parsed: ProgramParseResult) =
    match parsed with
        | ProgramParseResult { program = ProgramSyntax { items = items, body = body }, diagnostics = diagnostics } ->
            match diagnostics with
                | _diagnostic :: _rest -> Ok(Unit)
                | [] ->
                    if containsExternal(items)
                    then Error(InlineModuleExternal(moduleName))
                    else
                        match body with
                            | Some(_) -> Error(InlineModuleTrailingExpression(moduleName))
                            | None -> Ok(Unit)

let validateInlineModuleBody moduleName source =
    if "\n"
    |> Ashes.Text.split(source)
    |> containsImportLine
    then Error(InlineModuleImport(moduleName))
    else
        source
        |> parseProgram
        |> validateParsedBody(moduleName)

let composeModuleName scope name =
    if scope == ""
    then name
    else scope + "." + name

let recursive directModuleNames modules =
    match modules with
        | [] -> []
        | DirectInlineModule { name = name, body = _body } :: rest -> name :: directModuleNames(rest)

let publishInlineModule name source = deepCopy(InlineModuleInfo(name = name, source = source))

let finishInlineModuleExpansion modules source =
    InlineModuleExpansion(source = source, modules = modules)
    |> deepCopy
    |> Ok

let finishExpandedModules outerSource result =
    match result with
        | Error(error) -> Error(error)
        | Ok(expandedModules) -> finishInlineModuleExpansion(expandedModules)(outerSource)

let recursive expandCollectedModules scope childNames modules =
    match modules with
        | [] -> Ok([])
        | DirectInlineModule { name = name, body = body } :: rest ->
            let composedName = composeModuleName(scope)(name)
            in
                let blockSource =
                    body
                    |> dedent
                    |> rewriteInlineQualifiers(scope)(childNames)
                in
                    match validateInlineModuleBody(composedName)(blockSource) with
                        | Error(error) -> Error(error)
                        | Ok(_) ->
                            match expandInlineModules(composedName)(blockSource) with
                                | Error(error) -> Error(error)
                                | Ok(InlineModuleExpansion { source = expandedSource, modules = nestedModules }) ->
                                    match expandCollectedModules(scope)(childNames)(rest) with
                                        | Error(error) -> Error(error)
                                        | Ok(remaining) ->
                                            [publishInlineModule(composedName)(expandedSource)]
                                            |> appendList(nestedModules)
                                            |> (given (current) -> appendList(current)(remaining))
                                            |> Ok
and expandInlineModules scope source =
    (let normalized = normalizeCrLf(source)
    in
        let lines = Ashes.Text.split(normalized)("\n")
        in
            match collectInlineModules(scope)(lines)([])([])([]) with
                | Error(error) -> Error(error)
                | Ok(collection) ->
                    let modules = reverseList(collection.modules)
                    in
                        match modules with
                            | [] -> finishInlineModuleExpansion([])(source)
                            | _ ->
                                let childNames = directModuleNames(modules)
                                in
                                    let outerSource =
                                        collection.outer
                                        |> reverseList
                                        |> Ashes.Text.join("\n")
                                        |> rewriteInlineQualifiers(scope)(childNames)
                                        |> deepCopy
                                    in
                                        modules
                                        |> expandCollectedModules(scope)(childNames)
                                        |> finishExpandedModules(outerSource))
