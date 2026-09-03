import Ashes.IO
import Ashes.Test as test
import Ashes.Collection.List.map
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.DecisionSnapshot
import AshesCompiler.Semantics.ExplainReport
import AshesCompiler.Semantics.ExplainReportFormatter
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrExplainReporter
import AshesCompiler.Semantics.IrOptimizer
export (
    value runExplainReportTests,
)

// The shared parity fixtures: `<name>.source` is an ordinary program and `<name>.<kind>.txt` the
// text stage 0's reporter and formatter render for it from the same un-stitched lowering
// (`SelfhostIrParityTests` in `src/Ashes.Tests` writes both).
let fixtureRoot = "selfhost/parity/semantics"

let readFixture (path: Str) =
    match Ashes.IO.File.readText(path) with
        | Ok(text) -> text
        | Error(message) -> test.fail("could not read parity fixture " + path + ": " + message)

let fixtureSource (name: Str) = readFixture(fixtureRoot + "/lowered-ir/" + name + ".source")

let expectedReport (name: Str) (kindName: Str) = readFixture(fixtureRoot + "/explain/" + name + "." + kindName + ".txt")

let lowerFixture (name: Str) (source: Str) (program: ProgramSyntax) =
    match lowerCoreProgramWithSource(name + ".ash")(source)(program) with
        | CoreLoweringResult { program = Some(lowered), error = None } -> lowered
        | CoreLoweringResult { error = Some(error) } -> test.fail("lowering failed for " + name + ": " + Ashes.Trait.Show.show(error))
        | _ -> test.fail("lowering produced no program for " + name)

let parseFixture (name: Str) (source: Str) =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail(name + " should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let renderLines (lines: List(Str)) = Ashes.Text.join("\n")(lines) + "\n"

// The report the self-hosted pipeline renders for one fixture and kind: the parsed program and
// its lowering feed the decision snapshot, the optimized program feeds the RC counts, exactly as
// `ashes compile --explain` observes them.
let renderReport (name: Str) (kind: ExplainKind) =
    (let source = fixtureSource(name)
    in
        let program = parseFixture(name)(source)
        in
            let lowered = lowerFixture(name)(source)(program)
            in
                let request = explainRequestOf([kind])(None)
                in
                    lowered
                    |> optimizeIrProgram
                    |> (given (optimized) ->
                        buildExplainReport(captureDecisionSnapshot(given (_) -> None)(program)(lowered))(optimized)(request))
                    |> (given (report) -> formatExplainReport(report)(request))
                    |> renderLines)

let checkFixture (name: Str) (kind: ExplainKind) (kindName: Str) =
    (let expected = expectedReport(name)(kindName)
    in
        let actual = renderReport(name)(kind)
        in
            if actual == expected
            then Unit
            else test.fail("explain parity mismatch for " + name + "." + kindName + "\nexpected:\n" + expected + "actual:\n" + actual))

// A fixture/kind pair whose report legitimately differs from stage 0's because a decision is not
// captured by the self-hosted pipeline yet. `rewrite` maps stage 0's text to the text this pipeline
// is expected to produce, so the difference is pinned exactly; once the two agree the entry must be
// retired, which the first assertion enforces.
let checkKnownDifference (name: Str) (kind: ExplainKind) (kindName: Str) (reason: Str) rewrite =
    (let expected = expectedReport(name)(kindName)
    in
        let actual = renderReport(name)(kind)
        in
            if actual == expected
            then test.fail(name + "." + kindName + " now matches stage 0; retire its known difference (" + reason + ")")
            else
                if actual == rewrite(expected)
                then Unit
                else test.fail("unexpected explain difference for " + name + "." + kindName + " (" + reason + ")\nexpected:\n" + expected + "actual:\n" + actual))

let rewriteLines rewriteLine (text: Str) =
    "\n"
    |> Ashes.Text.split(text)
    |> rewriteLine
    |> Ashes.Text.join("\n")

let isRepresentationHeading (line: Str) = Ashes.Text.startsWith(line)("  representation [")

// Drops every `representation [...]` block: the heading and the indented count lines under it.
let recursive dropRepresentationBlocks (dropping: Bool) (lines: List(Str)) =
    match lines with
        | [] -> []
        | line :: rest ->
            if isRepresentationHeading(line)
            then dropRepresentationBlocks(true)(rest)
            else
                if dropping && Ashes.Text.startsWith(line)("    ")
                then dropRepresentationBlocks(true)(rest)
                else line :: dropRepresentationBlocks(false)(rest)

let withoutRepresentation (text: Str) =
    rewriteLines(dropRepresentationBlocks(false))(text)

let representationNotCaptured = "value placements are not captured by the self-hosted lowering, so the memory report has no representation blocks"

let uniqueYes (line: Str) =
    if line == "      unique:    no"
    then "      unique:    yes"
    else line

let everyParameterUnique (text: Str) =
    rewriteLines(map(uniqueYes))(text)

let moveSafetyProofsNotPorted = "move-safety proofs are not ported, so every parameter is reported unique"

let recursiveGroupNotPorted = "recursive-group lowering parity is not ported, so no dispatch or wrapper allocation is counted"

let noRcOperations (_text: Str) = "RC report\n=========\n\n  (no functions matched)\n"

let checkAllKinds (name: Str) =
    Unit
    |> (given (_) -> checkFixture(name)(ExplainOwnership)("ownership"))
    |> (given (_) -> checkFixture(name)(ExplainRc)("rc"))
    |> (given (_) -> checkFixture(name)(ExplainReuse)("reuse"))
    |> (given (_) -> checkFixture(name)(ExplainMemory)("memory"))

let checkClosureCapture unit =
    unit
    |> (given (_) -> checkFixture("closure_capture")(ExplainOwnership)("ownership"))
    |> (given (_) -> checkFixture("closure_capture")(ExplainRc)("rc"))
    |> (given (_) -> checkFixture("closure_capture")(ExplainReuse)("reuse"))
    |> (given (_) -> checkKnownDifference("closure_capture")(ExplainMemory)("memory")(representationNotCaptured)(withoutRepresentation))

let checkMutualRecursion unit =
    unit
    |> (given (_) -> checkKnownDifference("mutual_recursion")(ExplainOwnership)("ownership")(moveSafetyProofsNotPorted)(everyParameterUnique))
    |> (given (_) -> checkKnownDifference("mutual_recursion")(ExplainRc)("rc")(recursiveGroupNotPorted)(noRcOperations))
    |> (given (_) -> checkFixture("mutual_recursion")(ExplainReuse)("reuse"))
    |> (given (_) -> checkKnownDifference("mutual_recursion")(ExplainMemory)("memory")(representationNotCaptured)(withoutRepresentation))

let runExplainReportTests unit =
    unit
    |> (given (_) -> checkAllKinds("simple_arith"))
    |> (given (_) -> checkAllKinds("let_bindings"))
    |> (given (_) -> checkAllKinds("nested_let_scopes"))
    |> (given (_) -> checkAllKinds("scalar_match"))
    |> (given (_) -> checkAllKinds("ownerless_match"))
    |> (given (_) -> checkAllKinds("pattern_match"))
    |> checkClosureCapture
    |> checkMutualRecursion
    |> (given (_) -> Ashes.IO.print("all explain report tests passed"))
