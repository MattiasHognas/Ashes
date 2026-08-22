// Selects source and generated functions consistently for IR dumps and compiler reports.
//
// Invariants:
// - No selector selects every function.
// - Matching is an ASCII-case-insensitive substring across every user-recognizable function name.
// - Generated helpers remain selectable through their source or immediate generated parent.

import AshesCompiler.Semantics.IrOrigins
export (
    value matchesSourceFunction,
    value matchesIrFunction,
)

let containsIgnoringAsciiCase candidate filter =
    Ashes.Text.contains(
        Ashes.Text.asciiLower(candidate)
    )(
        Ashes.Text.asciiLower(filter)
    )

let maybeContains candidate filter =
    match candidate with
        | None -> false
        | Some(text) -> containsIgnoringAsciiCase(text)(filter)

let recursive anyTrue values =
    match values with
        | [] -> false
        | true :: _rest -> true
        | false :: rest -> anyTrue(rest)

let sourceNameMatches origin filter =
    match origin with
        | None -> false
        | Some(SourceFunctionOrigin { functionSourceName = sourceName, functionQualifiedName = qualifiedName }) ->
            anyTrue([
                containsIgnoringAsciiCase(sourceName)(filter),
                maybeContains(qualifiedName)(filter)
            ])

let matchesSourceFunction origin functionName filter =
    match filter with
        | None -> true
        | Some(text) ->
            anyTrue([
                containsIgnoringAsciiCase(functionName)(text),
                sourceNameMatches(origin)(text)
            ])

let generatedOriginValueMatches origin filter =
    match origin with
        | IrFunctionOrigin { generatedLabel = label, sourceOrigin = source, parentGeneratedLabel = parent } ->
            anyTrue([
                containsIgnoringAsciiCase(label)(filter),
                sourceNameMatches(source)(filter),
                maybeContains(parent)(filter)
            ])

let generatedOriginMatches origin filter =
    match origin with
        | None -> false
        | Some(value) -> generatedOriginValueMatches(value)(filter)

let matchesIrFunction origin label filter =
    match filter with
        | None -> true
        | Some(text) ->
            anyTrue([
                containsIgnoringAsciiCase(label)(text),
                generatedOriginMatches(origin)(text)
            ])
