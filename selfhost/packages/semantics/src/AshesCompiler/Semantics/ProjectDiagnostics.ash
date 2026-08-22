// Canonicalizes structured diagnostics emitted while a project is discovered and compiled.
//
// Invariants:
// - Source groups retain deterministic project discovery/plan order across files.
// - Diagnostics within a source are ordered by UTF-8 span and retain emission order for exact ties.
// - File attribution and the original structured diagnostic remain available to later CLI/LSP phases.

import Ashes.Collection.List.append
import Ashes.Collection.List.sortBy
import AshesCompiler.Frontend.Token
export (
    type ProjectDiagnosticSource(..),
    type ProjectDiagnostic(..),
    value orderProjectDiagnostics,
)

type ProjectDiagnosticSource =
    | sourcePath: Str
    | diagnostics: List(DiagnosticEntry)
    deriving {Eq, Show}

type ProjectDiagnostic =
    | sourcePath: Str
    | sourceOrder: Int
    | emissionOrder: Int
    | diagnostic: DiagnosticEntry
    deriving {Eq, Show}

let recursive diagnosticsForSource sourcePath sourceOrder emissionOrder diagnostics =
    match diagnostics with
        | [] -> []
        | diagnostic :: rest ->
            ProjectDiagnostic(
                sourcePath = sourcePath,
                sourceOrder = sourceOrder,
                emissionOrder = emissionOrder,
                diagnostic = diagnostic
            ) :: diagnosticsForSource(sourcePath)(sourceOrder)(emissionOrder + 1)(rest)

let recursive collectProjectDiagnostics sourceOrder sources =
    match sources with
        | [] -> []
        | ProjectDiagnosticSource { sourcePath = sourcePath, diagnostics = diagnostics } :: rest ->
            append(
                diagnosticsForSource(sourcePath)(sourceOrder)(0)(diagnostics),
                collectProjectDiagnostics(sourceOrder + 1)(rest)
            )

let diagnosticStart value =
    match value with
        | ProjectDiagnostic { diagnostic = DiagnosticEntry { span = TextSpan { start = start } } } -> start

let diagnosticEnd value =
    match value with
        | ProjectDiagnostic { diagnostic = DiagnosticEntry { span = TextSpan { end = end } } } -> end

let diagnosticSourceOrder value =
    match value with
        | ProjectDiagnostic { sourceOrder = sourceOrder } -> sourceOrder

let diagnosticEmissionOrder value =
    match value with
        | ProjectDiagnostic { emissionOrder = emissionOrder } -> emissionOrder

let intOrder left right =
    match (left < right, left > right) with
        | (true, _) -> -1
        | (_, true) -> 1
        | (false, false) -> 0

let projectDiagnosticBefore left right =
    match (right
    |> diagnosticSourceOrder
    |> intOrder(diagnosticSourceOrder(left)), right
    |> diagnosticStart
    |> intOrder(diagnosticStart(left)), right
    |> diagnosticEnd
    |> intOrder(diagnosticEnd(left)), right
    |> diagnosticEmissionOrder
    |> intOrder(diagnosticEmissionOrder(left))) with
        | (-1, _, _, _) -> true
        | (1, _, _, _) -> false
        | (0, -1, _, _) -> true
        | (0, 1, _, _) -> false
        | (0, 0, -1, _) -> true
        | (0, 0, 1, _) -> false
        | (0, 0, 0, order) -> order <= 0
        | (_, _, _, _) -> false

let orderProjectDiagnostics sources =
    sources
    |> collectProjectDiagnostics(0)
    |> sortBy(projectDiagnosticBefore)
