// Unit tests for OPT-42's `f$reuse` whole-function reuse specialization: the top-level candidate
// scan (`AshesCompiler.Semantics.ReuseFunctionSpecialization`) and the call-site qualification and
// generation CoreLowering.ash hangs off it. A qualifying call lowers to a specialization of the
// callee whose accumulator parameter is a linear reuse root, so the rebuild in its match arm
// overwrites the matched cell instead of allocating a fresh one.

import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.ReuseFunctionSpecialization
import AshesCompiler.Semantics.TcoAnalysis.collectInnermostBody
export (
    value runReuseFunctionSpecializationTests,
)

let parsed (source: Str) =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let lowered (source: Str) =
    match source
    |> parsed
    |> lowerCoreProgram with
        | CoreLoweringResult { program = Some(program), error = None } -> program
        | CoreLoweringResult { error = Some(error) } -> test.fail("program lowering failed: " + Ashes.Trait.Show.show(error))
        | _ -> test.fail("program lowering produced no program")

let dumped (source: Str) =
    source
    |> lowered
    |> (given (program: IrProgram) -> formatIr(program)(LoweredIr)(None))
    |> Ashes.Text.join("\n")

let dumpedLines (source: Str) =
    source
    |> lowered
    |> (given (program: IrProgram) -> formatIr(program)(LoweredIr)(None))

let containsText (needle: Str) (haystack: Str) = Ashes.Text.contains(haystack)(needle)

let recursive lookupCandidateValue (name: Str) (candidates: List((Str, List(Str), Expr))) =
    match candidates with
        | [] -> None
        | (candidate, parameters, value) :: rest ->
            if candidate == name
            then Some((parameters, value))
            else lookupCandidateValue(name)(rest)

let recursive coreCandidateCount (candidates: List((Str, List(Str), Expr))) =
    match candidates with
        | [] -> 0
        | _candidate :: rest -> 1 + coreCandidateCount(rest)

// A self-recursive rebuild of a copy-element list, called with a freshly built list: the shape
// stage 0 specializes (`doubleAll__reuse` in its own IR for the same program).
let freshListRebuildSource unit = "let recursive makeList (count: Int) =\n" + "    if count == 0\n" + "    then []\n" + "    else 7 :: makeList(count - 1)\n" + "\n" + "let recursive doubleAll (values: List(Int)) =\n" + "    match values with\n" + "        | [] -> []\n" + "        | value :: rest -> value * 2 :: doubleAll(rest)\n" + "\n" + "doubleAll(makeList(4))\n"

// The same call inside a tail-recursive driver, the shape a real workload takes: the accumulator
// is still a freshly built list at every iteration.
let loopedFreshListSource unit = "let recursive makeList (count: Int) =\n" + "    if count == 0\n" + "    then []\n" + "    else 7 :: makeList(count - 1)\n" + "\n" + "let recursive doubleAll (values: List(Int)) =\n" + "    match values with\n" + "        | [] -> []\n" + "        | value :: rest -> value * 2 :: doubleAll(rest)\n" + "\n" + "let recursive sumList (values: List(Int)) (total: Int) =\n" + "    match values with\n" + "        | [] -> total\n" + "        | value :: rest -> sumList(rest)(total + value)\n" + "\n" + "let recursive rounds (count: Int) (total: Int) =\n" + "    if count == 0\n" + "    then total\n" + "    else rounds(count - 1)(total + sumList(doubleAll(makeList(8)))(0))\n" + "\n" + "rounds(3)(0)\n"

// Two qualifying call sites of the same function: each generates its own specialization, and the
// second takes a suffixed label rather than colliding with the first.
let twoCallSiteSource unit = "let recursive makeList (count: Int) =\n" + "    if count == 0\n" + "    then []\n" + "    else 7 :: makeList(count - 1)\n" + "\n" + "let recursive doubleAll (values: List(Int)) =\n" + "    match values with\n" + "        | [] -> []\n" + "        | value :: rest -> value * 2 :: doubleAll(rest)\n" + "\n" + "let recursive sumList (values: List(Int)) (total: Int) =\n" + "    match values with\n" + "        | [] -> total\n" + "        | value :: rest -> sumList(rest)(total + value)\n" + "\n" + "sumList(doubleAll(makeList(3)))(0) + sumList(doubleAll(makeList(4)))(0)\n"

// The same rebuild handed a list threaded through an accumulator parameter: the callee's result
// reaches that parameter, so the argument is not provably fresh and no specialization is built.
let threadedListSource unit = "let recursive buildList (count: Int) (acc: List(Int)) =\n" + "    if count == 0\n" + "    then acc\n" + "    else buildList(count - 1)(count :: acc)\n" + "\n" + "let recursive doubleAll (values: List(Int)) =\n" + "    match values with\n" + "        | [] -> []\n" + "        | value :: rest -> value * 2 :: doubleAll(rest)\n" + "\n" + "doubleAll(buildList(4)([]))\n"

// A heap element: the rebuild passes each element straight through into the reused cell, so the
// list's own layout constrains nothing and stage 0 specializes it too.
let heapElementSource unit = "let recursive makeList (count: Int) =\n" + "    if count == 0\n" + "    then []\n" + "    else \"x\" :: makeList(count - 1)\n" + "\n" + "let recursive keepAll (values: List(Str)) =\n" + "    match values with\n" + "        | [] -> []\n" + "        | value :: rest -> value :: keepAll(rest)\n" + "\n" + "keepAll(makeList(4))\n"

// The accumulator is the LAST of several parameters; the earlier ones are configuration the
// specialization carries unchanged. Stage 0's own `moveBodies` shape.
let multiParameterSource unit = "let recursive makeList (count: Int) =\n" + "    if count == 0\n" + "    then []\n" + "    else 7 :: makeList(count - 1)\n" + "\n" + "let recursive scaleAll (factor: Int) (values: List(Int)) =\n" + "    match values with\n" + "        | [] -> []\n" + "        | value :: rest -> value * factor :: scaleAll(factor)(rest)\n" + "\n" + "scaleAll(3)(makeList(4))\n"

// The accumulator is a loop parameter rather than a fresh call result, and a named ADT whose every
// constructor field is the accumulator itself or a copy type — the shape stage 0 routes through
// `QualifyVariableReuseSpecialization` after its loop-entry copy makes the parameter unique.
let loopAccumulatorSource unit = "type Tree =\n" + "    | Leaf\n" + "    | Node(Tree, Int, Tree)\n" + "\n" + "let recursive bump (tree: Tree) =\n" + "    match tree with\n" + "        | Leaf -> Leaf\n" + "        | Node(left, value, right) -> Node(bump(left), value + 1, bump(right))\n" + "\n" + "let recursive makeTree (depth: Int) =\n" + "    if depth == 0\n" + "    then Leaf\n" + "    else Node(makeTree(depth - 1), depth, makeTree(depth - 1))\n" + "\n" + "let recursive rounds (count: Int) (acc: Tree) =\n" + "    if count == 0\n" + "    then acc\n" + "    else rounds(count - 1)(bump(acc))\n" + "\n" + "rounds(3)(makeTree(3))\n"

// A reader rather than a rewriter: its result is not the accumulator's type, so routing it
// through a specialization would allocate its result where nothing reclaims it.
let readerSource unit = "let recursive makeList (count: Int) =\n" + "    if count == 0\n" + "    then []\n" + "    else 7 :: makeList(count - 1)\n" + "\n" + "let recursive countAll (values: List(Int)) =\n" + "    match values with\n" + "        | [] -> 0\n" + "        | _value :: rest -> 1 + countAll(rest)\n" + "\n" + "countAll(makeList(4))\n"

let testCandidateScanFindsSelfRecursiveTopLevelFunctions unit =
    match Unit
    |> freshListRebuildSource
    |> parsed with
        | ProgramSyntax { items = items } ->
            match reuseSpecializationCandidates(items) with
                | (firstName, firstParameters, _firstValue) :: (secondName, secondParameters, _secondValue) :: [] -> firstName + "(" + Ashes.Text.join(",")(firstParameters) + ") " + secondName + "(" + Ashes.Text.join(",")(secondParameters) + ")" |> test.assertEqual("makeList(count) doubleAll(values)")
                | other ->
                    test.fail("expected two specialization candidates, got " + Ashes.Text.fromInt(coreCandidateCount(other)))

let testLabelNamesFirstSpecializationBare unit =
    0
    |> reuseSpecializationLabel("doubleAll")
    |> test.assertEqual("doubleAll__reuse")

let testLabelSuffixesLaterSpecializations unit =
    2
    |> reuseSpecializationLabel("doubleAll")
    |> test.assertEqual("doubleAll__reuse$2")

let testFreshCopyListArgumentGeneratesSpecialization unit =
    Unit
    |> freshListRebuildSource
    |> dumped
    |> containsText("function doubleAll__reuse")
    |> test.assertEqual(true)

// The point of the specialization: its cons arm hands the matched cell out as an arena token and
// the rebuild overwrites that cell rather than allocating a fresh one.
let testSpecializationRebuildsListCellInPlace unit =
    Unit
    |> freshListRebuildSource
    |> dumped
    |> containsText("AllocReusing")
    |> test.assertEqual(true)

let testSpecializationPublishesListCellToken unit =
    Unit
    |> freshListRebuildSource
    |> dumped
    |> containsText("DropReuse")
    |> test.assertEqual(true)

let testLoopedCallGeneratesSpecialization unit =
    Unit
    |> loopedFreshListSource
    |> dumped
    |> containsText("function doubleAll__reuse")
    |> test.assertEqual(true)

let testSecondCallSiteTakesASuffixedLabel unit =
    Unit
    |> twoCallSiteSource
    |> dumped
    |> containsText("function doubleAll__reuse$1")
    |> test.assertEqual(true)

let testThreadedListArgumentKeepsOrdinaryCall unit =
    Unit
    |> threadedListSource
    |> dumped
    |> containsText("__reuse")
    |> test.assertEqual(false)

let testHeapElementListGeneratesSpecialization unit =
    Unit
    |> heapElementSource
    |> dumped
    |> containsText("function keepAll__reuse")
    |> test.assertEqual(true)

let testMultiParameterCandidateGeneratesSpecialization unit =
    Unit
    |> multiParameterSource
    |> dumped
    |> containsText("function scaleAll__reuse")
    |> test.assertEqual(true)

let testMultiParameterSpecializationReusesListCell unit =
    Unit
    |> multiParameterSource
    |> dumped
    |> containsText("AllocReusing")
    |> test.assertEqual(true)

let recursive describeSpecializationAccumulators (found: List((Str, Str))) =
    match found with
        | [] -> ""
        | (accumulator, callee) :: rest -> accumulator + "->" + callee + describeSpecializationAccumulators(rest)

// The loop-parameter scan on its own: `rounds`'s body hands `acc` straight to the single-parameter
// candidate `bump`.
let testLoopScanFindsSpecializationAccumulator unit =
    match Unit
    |> loopAccumulatorSource
    |> parsed with
        | ProgramSyntax { items = items } ->
            match reuseSpecializationCandidates(items) with
                | candidates ->
                    match lookupCandidateValue("rounds")(candidates) with
                        | Some((parameters, value)) ->
                            value
                            |> collectInnermostBody
                            |> (given (body: Expr) -> collectSpecializableCallArgs(parameters)(["bump"])(body)([]))
                            |> describeSpecializationAccumulators
                            |> test.assertEqual("acc->bump")
                        | None -> test.fail("rounds should be a specialization candidate")

let testLoopAccumulatorGeneratesSpecialization unit =
    Unit
    |> loopAccumulatorSource
    |> dumped
    |> containsText("function bump__reuse")
    |> test.assertEqual(true)

// The back edge of a loop whose accumulator a fully-reusing specialization rewrites in place
// reclaims its arena, which only the reset-safety verdict licenses: the reclaim sits immediately
// before the loop's stack-pointer restore, a pairing no other reset in the program produces.
let recursive linesHaveAdjacent (first: Str) (second: Str) (lines: List(Str)) =
    match lines with
        | [] -> false
        | line :: rest ->
            match rest with
                | [] -> false
                | next :: _remaining -> Ashes.Text.contains(line)(first) && Ashes.Text.contains(next)(second) || linesHaveAdjacent(first)(second)(rest)

let testLoopBackEdgeReclaimsItsArena unit =
    Unit
    |> loopAccumulatorSource
    |> dumpedLines
    |> linesHaveAdjacent("ReclaimArenaChunks")("RestoreStackPointer")
    |> test.assertEqual(true)

let testLoopAccumulatorSpecializationReusesCells unit =
    Unit
    |> loopAccumulatorSource
    |> dumped
    |> containsText("AllocReusing")
    |> test.assertEqual(true)

let testReaderKeepsOrdinaryCall unit =
    Unit
    |> readerSource
    |> dumped
    |> containsText("__reuse")
    |> test.assertEqual(false)

let reportSuccess unit = Ashes.IO.print("all self-hosted reuse function specialization tests passed")

let runReuseFunctionSpecializationTests unit =
    unit
    |> testCandidateScanFindsSelfRecursiveTopLevelFunctions
    |> testLabelNamesFirstSpecializationBare
    |> testLabelSuffixesLaterSpecializations
    |> testFreshCopyListArgumentGeneratesSpecialization
    |> testSpecializationRebuildsListCellInPlace
    |> testSpecializationPublishesListCellToken
    |> testLoopedCallGeneratesSpecialization
    |> testSecondCallSiteTakesASuffixedLabel
    |> testThreadedListArgumentKeepsOrdinaryCall
    |> testHeapElementListGeneratesSpecialization
    |> testMultiParameterCandidateGeneratesSpecialization
    |> testMultiParameterSpecializationReusesListCell
    |> testLoopScanFindsSpecializationAccumulator
    |> testLoopAccumulatorGeneratesSpecialization
    |> testLoopAccumulatorSpecializationReusesCells
    |> testLoopBackEdgeReclaimsItsArena
    |> testReaderKeepsOrdinaryCall
    |> reportSuccess
