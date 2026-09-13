// Finds the shipped standard-library modules a source reaches through bare qualified references
// (`Ashes.Text.join(...)` with no `import Ashes.Text`), language.md's "qualified access, no import
// required", so a stitcher can load them the way it loads imported modules. Mirrors stage 0's
// `CollectQualifiedStdModuleReferences`/`NeedsStandardLibrarySource`.
//
// Invariants:
// - The scan is over the token stream: every maximal `Ashes.A.B.c` identifier path, wherever it
//   occurs (expressions, types, patterns), yields one reference to the LONGEST known module prefix
//   and the member segment right after it (`Ashes.IO.Path.join` -> `Ashes.IO.Path`, `join`).
// - A member the builtin table lowers intrinsically (`Ashes.Text.fromInt`) needs no source; only a
//   reference to a member outside the table, into a module that has a shipped text, asks for it.
// - Lexer diagnostics are ignored here: a source that fails to lex fails in the parser, where the
//   diagnostic belongs.

import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
import AshesCompiler.Frontend.Lexer
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.CoreBuiltinLowering
export (
    type QualifiedShippedReference(..),
    value collectQualifiedShippedReferences,
    value qualifiedShippedModulesNeedingSource,
)

type QualifiedShippedReference =
    | referenceModule: Str
    | referenceMember: Str
    deriving {Eq, Show}

type QualifiedScanState =
    | scanPath: Str
    | scanLongestModule: Maybe(Str)
    | scanAfterDot: Bool
    | scanReferences: List(QualifiedShippedReference)

let recursive containsText (value: Str) (values: List(Str)) =
    match values with
        | [] -> false
        | candidate :: rest ->
            if candidate == value
            then true
            else containsText(value)(rest)

// The segment of `path` right after `prefix.`, or empty when the path ends at the prefix.
let memberAfter (prefix: Str) (path: Str) =
    (let start = Ashes.Text.length(prefix) + 1
    in
        if start >= Ashes.Text.length(path)
        then ""
        else
            match Ashes.Text.split(Ashes.Text.drop(path)(start))(".") with
                | [] -> ""
                | member :: _rest -> member)

// Closes the path being scanned: a reference is recorded only when some prefix named a known
// module.
let flushPath (state: QualifiedScanState) =
    match state with
        | QualifiedScanState { scanPath = path, scanLongestModule = Some(moduleName), scanReferences = reversed } ->
            QualifiedScanState(
                scanPath = "",
                scanLongestModule = None,
                scanAfterDot = false,
                scanReferences = QualifiedShippedReference(referenceModule = moduleName, referenceMember = memberAfter(moduleName)(path)) :: reversed
            )
        | QualifiedScanState { scanReferences = reversed } -> QualifiedScanState(scanPath = "", scanLongestModule = None, scanAfterDot = false, scanReferences = reversed)

let startPath (text: Str) (state: QualifiedScanState) =
    match state with
        | QualifiedScanState { scanReferences = reversed } ->
            QualifiedScanState(
                scanPath = if text == "Ashes"
                then "Ashes"
                else "",
                scanLongestModule = None,
                scanAfterDot = false,
                scanReferences = reversed
            )

let extendPath (known: List(Str)) (text: Str) (state: QualifiedScanState) =
    match state with
        | QualifiedScanState { scanPath = path, scanLongestModule = longestModule, scanReferences = reversed } ->
            let candidate = path + "." + text
            in
                QualifiedScanState(
                    scanPath = candidate,
                    scanLongestModule = if containsText(candidate)(known)
                    then Some(candidate)
                    else longestModule,
                    scanAfterDot = false,
                    scanReferences = reversed
                )

let recursive scanTokens (known: List(Str)) (tokens: List(Token)) (state: QualifiedScanState) =
    match tokens with
        | [] ->
            match flushPath(state) with
                | QualifiedScanState { scanReferences = reversed } -> reverseList(reversed)
        | Token { kind = kind, text = text } :: rest ->
            match (kind, state.scanAfterDot, state.scanPath == "") with
                | (Ident, true, _) ->
                    state
                    |> extendPath(known)(text)
                    |> scanTokens(known)(rest)
                | (Dot, false, false) -> scanTokens(known)(rest)((state with scanAfterDot = true))
                | (Ident, _, _) ->
                    state
                    |> flushPath
                    |> startPath(text)
                    |> scanTokens(known)(rest)
                | (EOF, _, _) ->
                    state
                    |> flushPath
                    |> scanTokens(known)(rest)
                | (_other, _, _) ->
                    state
                    |> flushPath
                    |> scanTokens(known)(rest)

// Every bare qualified reference into a module named in `known`, in source order, repeats kept.
let collectQualifiedShippedReferences (known: List(Str)) (source: Str) =
    match tokenize(source) with
        | LexerResult { tokens = tokens } -> scanTokens(known)(tokens)(QualifiedScanState(scanPath = "", scanLongestModule = None, scanAfterDot = false, scanReferences = []))

let recursive dedupe (names: List(Str)) (seen: List(Str)) =
    match names with
        | [] -> reverseList(seen)
        | name :: rest ->
            if containsText(name)(seen)
            then dedupe(rest)(seen)
            else dedupe(rest)(name :: seen)

let needsShippedSource (shippedNames: List(Str)) (reference: QualifiedShippedReference) =
    match reference with
        | QualifiedShippedReference { referenceModule = moduleName, referenceMember = memberName } ->
            match coreBuiltinKind(moduleName)(memberName) with
                | Some(_kind) -> false
                | None -> containsText(moduleName)(shippedNames)

let recursive sourcesNeeded (shippedNames: List(Str)) (references: List(QualifiedShippedReference)) =
    match references with
        | [] -> []
        | reference :: rest ->
            if needsShippedSource(shippedNames)(reference)
            then reference.referenceModule :: sourcesNeeded(shippedNames)(rest)
            else sourcesNeeded(shippedNames)(rest)

// The shipped modules (named in `shippedNames`, the texts a stitcher can resolve) that `source`
// reaches only through bare qualified references to members the builtin table does not lower
// itself, each once, in first-reference order. Intrinsic modules count as known prefixes so a
// reference such as `Ashes.IO.print` is attributed to `Ashes.IO`, not left dangling.
let qualifiedShippedModulesNeedingSource (shippedNames: List(Str)) (source: Str) =
    source
    |> collectQualifiedShippedReferences(appendList(shippedNames)(intrinsicBuiltinModuleNames))
    |> sourcesNeeded(shippedNames)
    |> (given (names) -> dedupe(names)([]))
