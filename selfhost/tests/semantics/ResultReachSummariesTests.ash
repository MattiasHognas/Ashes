// Unit tests for the whole-program result-reach summaries: the registry of top-level and nested
// functions, the call-site substitution of a callee's summary, and the poison and whole-versus-
// component verdicts stage 0's ownership report prints.

import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.ResultReachSummaries
import Ashes.Collection.List.length
export (
    value runResultReachSummariesTests,
)

let parsedProgram source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let summariesOf source =
    source
    |> parsedProgram
    |> programReachSummaries

// Whether a function's nesting matches the lookup: top-level for `None`, nested for `Some`.
let nestingMatches (enclosing: Maybe(Str)) (candidateEnclosing: Maybe(Str)) =
    match (enclosing, candidateEnclosing) with
        | (None, None) -> true
        | (Some(_parent), Some(_candidateParent)) -> true
        | _ -> false

let recursive summaryNamed (name: Str) (enclosing: Maybe(Str)) (summaries: List(ReachSummary)) =
    match summaries with
        | [] -> test.fail("expected a summary for " + name)
        | (ReachSummary { function = ReachFunction { name = candidate, enclosing = candidateEnclosing } } as summary) :: rest ->
            if candidate == name && nestingMatches(enclosing)(candidateEnclosing)
            then summary
            else summaryNamed(name)(enclosing)(rest)

let factsOf (summary: ReachSummary) =
    match summary with
        | ReachSummary { reach = reach } -> reachFactsOf(reach)

let parametersOf (summary: ReachSummary) =
    match summary with
        | ReachSummary { function = ReachFunction { parameters = parameters } } -> parameters

let appendSource = "let append left right =\n    (let recursive go rest =\n        match rest with\n            | [] -> right\n            | head :: tail -> head :: go(tail)\n    in go(left))\n"

// `append`'s result keeps only parts of `left` (the heads `go` re-conses) and is poisoned by the
// captured `right` the local function returns, which the summary never lists as an alias.
let testLocalRecursiveCalleeSubstitutesItsSummary unit =
    (let facts =
        appendSource
        |> summariesOf
        |> summaryNamed("append")(None)
        |> factsOf
    in
        "left"
        |> resultReachesParameter(facts)
        |> test.assertEqual(true)
        |> (given (_) ->
            "left"
            |> resultReachesParameterWhole(facts)
            |> test.assertEqual(false))
        |> (given (_) ->
            "right"
            |> resultReachesParameter(facts)
            |> test.assertEqual(false))
        |> (given (_) ->
            facts
            |> isResultPoisoned
            |> test.assertEqual(true)))

// The nested `go` is registered under its parent: its result keeps components of `rest` and the
// free `right` poisons it.
let testNestedFunctionIsRegisteredUnderItsParent unit =
    (let facts =
        appendSource
        |> summariesOf
        |> summaryNamed("go")(Some("append"))
        |> factsOf
    in
        "rest"
        |> resultReachesParameter(facts)
        |> test.assertEqual(true)
        |> (given (_) ->
            "rest"
            |> resultReachesParameterWhole(facts)
            |> test.assertEqual(false))
        |> (given (_) ->
            facts
            |> isResultPoisoned
            |> test.assertEqual(true)))

let mapSource = "let map f =\n    (let recursive mapGo xs =\n        match xs with\n            | [] -> []\n            | head :: tail -> f(head) :: mapGo(tail)\n    in mapGo)\n"

// The Map.set shape registers `map` over its outer parameter and the accumulator; the call
// through the parameter `f` poisons and nothing is reached whole.
let testNestedRecursiveReturnShapeTakesTheAccumulator unit =
    (let summary =
        mapSource
        |> summariesOf
        |> summaryNamed("map")(None)
    in
        summary
        |> parametersOf
        |> test.assertEqual(["f", "xs"])
        |> (given (_) ->
            summary
            |> factsOf
            |> isResultPoisoned
            |> test.assertEqual(true))
        |> (given (_) ->
            "xs"
            |> resultReachesParameter(factsOf(summary))
            |> test.assertEqual(false)))

// A saturated call substitutes the callee's whole reach for the argument: `keep` stores its
// parameter in a cell, so `wrap`'s result keeps `value` itself.
let testSaturatedCallSubstitutesWholeReach unit =
    (let facts =
        "let keep item = [item]\nlet wrap value = keep(value)\n"
        |> summariesOf
        |> summaryNamed("wrap")(None)
        |> factsOf
    in
        "value"
        |> resultReachesParameterWhole(facts)
        |> test.assertEqual(true)
        |> (given (_) ->
            facts
            |> isResultPoisoned
            |> test.assertEqual(false)))

// A callee rebuilding its parameter from parts hands the caller only components of the argument.
let testCallThroughRebuildingCalleeReachesByComponent unit =
    (let facts =
        "let recursive rebuild xs =\n    match xs with\n        | [] -> []\n        | head :: tail -> head :: rebuild(tail)\nlet copy values = rebuild(values)\n"
        |> summariesOf
        |> summaryNamed("copy")(None)
        |> factsOf
    in
        "values"
        |> resultReachesParameter(facts)
        |> test.assertEqual(true)
        |> (given (_) ->
            "values"
            |> resultReachesParameterWhole(facts)
            |> test.assertEqual(false))
        |> (given (_) ->
            facts
            |> isResultPoisoned
            |> test.assertEqual(false)))

// A free name is a top-level or enclosing binding and poisons; a call through a name nothing in
// scope declares is not modelled and poisons.
let testFreeNameAndUnknownCalleePoison unit =
    (let summaries = summariesOf("let shared = [1]\nlet reuse x = shared\nlet delegate y = unknown(y)\n")
    in
        summaries
        |> summaryNamed("reuse")(None)
        |> factsOf
        |> isResultPoisoned
        |> test.assertEqual(true)
        |> (given (_) ->
            summaries
            |> summaryNamed("delegate")(None)
            |> factsOf
            |> isResultPoisoned
            |> test.assertEqual(true)))

// A scalar field of a constructor holds its value inline and never aliases the parameter.
let testCopyTypedConstructorFieldDoesNotReach unit =
    (let facts =
        "type Counter =\n    | Counter(Int, Str)\nlet make n label = Counter(n)(label)\n"
        |> summariesOf
        |> summaryNamed("make")(None)
        |> factsOf
    in
        "n"
        |> resultReachesParameter(facts)
        |> test.assertEqual(false)
        |> (given (_) ->
            "label"
            |> resultReachesParameterWhole(facts)
            |> test.assertEqual(true)))

// Two simultaneous positions holding the same parameter poison the result as internally shared.
let testInternalSharingPoisons unit =
    (let facts =
        "let twice x = (x, x)\n"
        |> summariesOf
        |> summaryNamed("twice")(None)
        |> factsOf
    in
        facts
        |> isResultPoisoned
        |> test.assertEqual(true))

// A function's summary computed on its own, as the lowering falls back to for a callee outside
// the registry, still tells a returned parameter from a fresh result.
let testSingleFunctionReach unit =
    (let identity = parseExpression("x")
    in
        match identity with
            | ExpressionParseResult { expression = expression } ->
                expression
                |> singleFunctionReach(["x"])
                |> reachFactsOf
                |> resultReachesParameterWhole
                |> (given (reaches) ->
                    "x"
                    |> reaches
                    |> test.assertEqual(true)))

let reportResultReachSummariesSuccess unit = Ashes.IO.print("all self-hosted result reach summary tests passed")

let runResultReachSummariesTests unit =
    unit
    |> testLocalRecursiveCalleeSubstitutesItsSummary
    |> testNestedFunctionIsRegisteredUnderItsParent
    |> testNestedRecursiveReturnShapeTakesTheAccumulator
    |> testSaturatedCallSubstitutesWholeReach
    |> testCallThroughRebuildingCalleeReachesByComponent
    |> testFreeNameAndUnknownCalleePoison
    |> testCopyTypedConstructorFieldDoesNotReach
    |> testInternalSharingPoisons
    |> testSingleFunctionReach
    |> reportResultReachSummariesSuccess
