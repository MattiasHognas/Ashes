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

let recursive normalizeCrLf source =
    match Ashes.Text.unconsText(source) with
        | None -> ""
        | Some((head, tail)) ->
            if head == "\r"
            then
                match Ashes.Text.unconsText(tail) with
                    | Some(("\n", rest)) -> "\n" + normalizeCrLf(rest)
                    | _ -> head + normalizeCrLf(tail)
            else head + normalizeCrLf(tail)

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

let collectedNames (collection: InlineModuleCollection) = collection.names

let collectedOuter (collection: InlineModuleCollection) = collection.outer

let collectedModules (collection: InlineModuleCollection) = collection.modules

let addCollectedModule name body collection =
    (let names = name :: collectedNames(collection)
    in
        let modules = DirectInlineModule(name = name, body = body) :: collectedModules(collection)
        in InlineModuleCollection(names = names, outer = collectedOuter(collection), modules = modules))

let addOuterLine line collection =
    (let outer = line :: collectedOuter(collection)
    in
        let names = collectedNames(collection)
        in InlineModuleCollection(names = names, outer = outer, modules = collectedModules(collection)))

let finishCollectedModule name body collection remaining = Ok((addCollectedModule(name)(body)(collection), remaining))

let collectInlineModule scope (header: InlineModuleHeader) remaining (collection: InlineModuleCollection) =
    if containsName(header.name)(collection.names)
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
                    | InlineModuleBody { lines = body, remaining = afterBody } ->
                        finishCollectedModule(
                            header.name,
                            body,
                            collection,
                            afterBody
                        )

let recursive collectInlineModules scope lines (collection: InlineModuleCollection) =
    match lines with
        | [] -> Ok(collection)
        | line :: rest ->
            match parseInlineModuleHeader(line) with
                | None ->
                    collection
                    |> addOuterLine(line)
                    |> collectInlineModules(scope)(rest)
                | Some(header) ->
                    match collectInlineModule(scope)(header)(rest)(collection) with
                        | Error(error) -> Error(error)
                        | Ok((next, remaining)) -> collectInlineModules(scope)(remaining)(next)

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

let previousAllowsQualifier previous =
    match previous with
        | None -> true
        | Some(character) ->
            if isNameCharacter(character)
            then false
            else character != "."

let childMatchesQualifier source name =
    (let prefix = name + "."
    in
        if Ashes.Text.startsWith(source)(prefix)
        then
            match source
            |> (given (text) ->
                prefix
                |> Ashes.Text.length
                |> Ashes.Text.drop(text))
            |> Ashes.Text.unconsText with
                | Some((next, _tail)) -> isIdentifierStart(next)
                | None -> false
        else false)

let recursive findQualifiedChild source childNames =
    match childNames with
        | [] -> None
        | name :: rest ->
            if childMatchesQualifier(source)(name)
            then Some(name)
            else findQualifiedChild(source)(rest)

let recursive rewriteQualifierText scope childNames source previous inString =
    match Ashes.Text.unconsText(source) with
        | None -> ""
        | Some((head, tail)) ->
            if inString
            then
                if head == "\\"
                then
                    match Ashes.Text.unconsText(tail) with
                        | None -> head
                        | Some((escaped, rest)) ->
                            head + escaped + rewriteQualifierText(
                                scope,
                                childNames,
                                rest,
                                Some(escaped),
                                true
                            )
                else
                    if head == "\""
                    then head + rewriteQualifierText(scope)(childNames)(tail)(Some(head))(false)
                    else head + rewriteQualifierText(scope)(childNames)(tail)(Some(head))(true)
            else
                if head == "\""
                then head + rewriteQualifierText(scope)(childNames)(tail)(Some(head))(true)
                else
                    if previousAllowsQualifier(previous)
                    then
                        match findQualifiedChild(source)(childNames) with
                            | Some(name) ->
                                let consumed = Ashes.Text.length(name) + 1
                                in
                                    scope + "." + name + "." + rewriteQualifierText(
                                        scope,
                                        childNames,
                                        Ashes.Text.drop(source)(consumed),
                                        Some("."),
                                        false
                                    )
                            | None -> head + rewriteQualifierText(scope)(childNames)(tail)(Some(head))(false)
                    else head + rewriteQualifierText(scope)(childNames)(tail)(Some(head))(false)

let rewriteInlineQualifiers scope childNames source =
    if scope == ""
    then source
    else rewriteQualifierText(scope)(childNames)(source)(None)(false)

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
        let initial = InlineModuleCollection(names = [], outer = [], modules = [])
        in
            match collectInlineModules(scope)(Ashes.Text.split(normalized)("\n"))(initial) with
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
