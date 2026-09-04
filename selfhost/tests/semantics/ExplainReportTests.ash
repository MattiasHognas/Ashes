import Ashes.IO
import Ashes.Test as test
import Ashes.Collection.List.append
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
        | CoreLoweringResult { program = Some(lowered), error = None, valuePlacements = valuePlacements } -> (lowered, valuePlacements)
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
let renderReportBody (name: Str) (kind: ExplainKind) (program: ProgramSyntax) (lowered: IrProgram) (valuePlacements: List((Int, Maybe(IrFunctionOrigin), Bool))) =
    (let request = explainRequestOf([kind])(None)
    in
        lowered
        |> optimizeIrProgram
        |> (given (optimized) ->
            buildExplainReport(captureDecisionSnapshot(given (_) -> None)(program)(lowered)(valuePlacements))(optimized)(request))
        |> (given (report) -> formatExplainReport(report)(request))
        |> renderLines)

let renderReport (name: Str) (kind: ExplainKind) =
    (let source = fixtureSource(name)
    in
        let program = parseFixture(name)(source)
        in
            match lowerFixture(name)(source)(program) with
                | (lowered, valuePlacements) -> renderReportBody(name)(kind)(program)(lowered)(valuePlacements))

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

let isParameterHeaderLine (name: Str) (line: Str) = line == "    " + name

// Flips the first `unique:` line after `name`'s own parameter header, from stage 0's `yes` to this
// port's `no`.
let recursive rewriteParameterUniqueToNo (name: Str) (armed: Bool) (lines: List(Str)) =
    match lines with
        | [] -> []
        | line :: rest ->
            if isParameterHeaderLine(name)(line)
            then line :: rewriteParameterUniqueToNo(name)(true)(rest)
            else
                if armed && line == "      unique:    yes"
                then "      unique:    no" :: rewriteParameterUniqueToNo(name)(false)(rest)
                else line :: rewriteParameterUniqueToNo(name)(armed)(rest)

let yCapturedThroughPartialApplicationAliasNotUnique (text: Str) =
    rewriteLines(rewriteParameterUniqueToNo("y")(false))(text)

let curriedPartialApplicationAliasNotTraced = "move safety does not trace a top-level value binding aliasing a curried partial application (`add5 = makeAdder(5)`, then `add5(10)`) back to makeAdder's own second parameter, so its call site looks under-applied"

let recursiveGroupNotPorted = "recursive-group lowering parity is not ported, so no dispatch or wrapper allocation is counted"

let noRcOperations (_text: Str) = "RC report\n=========\n\n  (no functions matched)\n"

// Line-oriented rewrites scoped to one or more `Function: <name>` blocks, so a fix that only
// applies to a specific function's result facts does not touch anything outside it.
let recursive isTargetFunctionHeading (targetNames: List(Str)) (line: Str) =
    match targetNames with
        | [] -> false
        | name :: rest ->
            if line == "Function: " + name
            then true
            else isTargetFunctionHeading(rest)(line)

let recursive scopedRewrite (targetNames: List(Str)) transform (inBlock: Bool) (lines: List(Str)) =
    match lines with
        | [] -> []
        | line :: rest ->
            if Ashes.Text.startsWith(line)("Function: ")
            then
                line :: scopedRewrite(targetNames)(transform)(isTargetFunctionHeading(targetNames)(line))(rest)
            else
                if inBlock
                then
                    rest
                    |> scopedRewrite(targetNames)(transform)(inBlock)
                    |> append(transform(line))
                else line :: scopedRewrite(targetNames)(transform)(inBlock)(rest)

// A pattern-extracted component (a record field or an ADT constructor's argument) is not tracked
// as reaching its parameter (see `analyzeMatchArmsReach` in OwnershipInference.ash): the result
// reads as fresh with no alias, rather than the whole-set reach stage 0 reports.
let componentReachLostLine (paramName: Str) (line: Str) =
    match line with
        | "    fresh:    no" -> ["    fresh:    yes"]
        | "    aliases:" -> ["    aliases:  (none)"]
        | _ ->
            if line == "      - " + paramName
            then []
            else [line]

let componentReachNotTracked (targetNames: List(Str)) (paramName: Str) (text: Str) =
    rewriteLines(scopedRewrite(targetNames)(componentReachLostLine(paramName))(false))(text)

let componentReachNotTrackedReason = "result-reach through a destructured pattern component is not tracked (documented in analyzeMatchArmsReach), so an extracted field does not mark its parameter reached"

// The memory report's condensed ownership line carries the same component-reach gap as
// `componentReachLostLine`, just rendered as a single "result fresh:" line with no aliases list.
let componentReachLostInMemoryLine (line: Str) =
    match line with
        | "    result fresh: no" -> ["    result fresh: yes"]
        | _ -> [line]

let componentReachNotTrackedInMemory (targetNames: List(Str)) (text: Str) =
    rewriteLines(scopedRewrite(targetNames)(componentReachLostInMemoryLine)(false))(text)

// Result reach is computed one function at a time, with no whole-program fixpoint: a callee's own
// poisoned (unmodelled) result reach does not propagate into a caller that returns a value built
// from calling it, so the caller reads as an ordinary (non-whole) reach of its own parameter
// instead of poisoned.
let poisonNotPropagatedLine (paramName: Str) (line: Str) =
    match line with
        | "    poisoned: yes" -> ["    poisoned: no"]
        | "    aliases:  (none)" -> ["    aliases:", "      - " + paramName]
        | _ -> [line]

let poisonNotPropagated (targetNames: List(Str)) (paramName: Str) (text: Str) =
    rewriteLines(scopedRewrite(targetNames)(poisonNotPropagatedLine(paramName))(false))(text)

let poisonNotPropagatedReason = "result reach has no whole-program fixpoint yet, so a poisoned callee's poison does not propagate into a caller that returns its result"

// Flips the second `Function: pair` heading (the optimizer's scalar-environment specialization of
// `pair`, correlated to the same source origin) to carry this port's own generated label.
let recursive rewriteSecondFunctionHeading (name: Str) (label: Str) (seen: Int) (lines: List(Str)) =
    match lines with
        | [] -> []
        | line :: rest ->
            if line == "Function: " + name
            then
                if seen == 1
                then "Function: " + name + " [" + label + "]" :: rewriteSecondFunctionHeading(name)(label)(seen + 1)(rest)
                else line :: rewriteSecondFunctionHeading(name)(label)(seen + 1)(rest)
            else line :: rewriteSecondFunctionHeading(name)(label)(seen)(rest)

let pairScalarEnvSpecializationLabeled (text: Str) =
    rewriteLines(rewriteSecondFunctionHeading("pair")("lambda_1__scalarenv0")(0))(text)

let scalarEnvSpecializationNotCorrelated = "the optimizer's scalar-environment specialization of pair reports its own generated label here rather than stage 0's shared origin label"

// Representation is classified by a post-hoc IR walk (DecisionSnapshot's classifyInstructionRepr)
// that tracks each local slot's representation flowing forward through StoreLocal/LoadLocal in
// straight-line instruction order, without regard to which branch actually produced it. The RC
// argument-normalization prologue stores to the same slot from both its Borrow arm and its
// CopyOutArena arm, so the walk lets the copy arm's runtime-rc representation overwrite the
// borrow arm's, and every later read of that slot inherits runtime-rc — even ordinary reads that
// never went through the copy. Stage 0 tags representation onto each value where it is produced
// instead of re-deriving it from slot flow, so only the value that actually came from the copy
// carries runtime rc.
let argNormalizePrologueMergesRcBranchLine (line: Str) =
    match line with
        | "    conservative unknown: 2" -> ["    conservative unknown: 1"]
        | "    runtime rc:           1" -> ["    runtime rc:           2"]
        | _ -> [line]

let argNormalizePrologueMergesRcBranch (targetNames: List(Str)) (text: Str) =
    rewriteLines(scopedRewrite(targetNames)(argNormalizePrologueMergesRcBranchLine)(false))(text)

let argNormalizePrologueMergesRcBranchReason = "representation is classified by a post-hoc IR walk that lets a stored local's representation flow past its control-flow join, so every read of an RC-argument-normalized local after the Borrow/CopyOutArena merge inherits the copy branch's runtime-rc representation instead of only the value that actually went through the copy"

let checkConsumedListArgument unit =
    unit
    |> (given (_) -> checkFixture("consumed_list_argument")(ExplainOwnership)("ownership"))
    |> (given (_) -> checkFixture("consumed_list_argument")(ExplainRc)("rc"))
    |> (given (_) -> checkFixture("consumed_list_argument")(ExplainReuse)("reuse"))
    |> (given (_) ->
        ["makeItems"]
        |> argNormalizePrologueMergesRcBranch
        |> checkKnownDifference("consumed_list_argument")(ExplainMemory)("memory")(argNormalizePrologueMergesRcBranchReason))

// A total match's overall result is a reload of whichever arm's own (already-recorded) result
// reached the shared result slot, classified by the same last-write-wins walk: since every arm,
// including the unreachable match_none fallback, writes to that slot in program order, the join
// read picks up the last write in program order rather than the reachable arm that actually
// produced the value, misclassifying the join read as conservative-unknown.
let matchResultSlotJoinLine (line: Str) =
    match line with
        | "    conservative unknown: 1" -> ["    conservative unknown: 2"]
        | _ -> [line]

let matchResultSlotJoinReason = "representation is classified by a post-hoc walk that is last-write-wins in program order rather than per-branch, so a total match's result-slot join read takes whichever arm wrote the slot last in program order (here the unreachable match_none fallback) instead of the reachable arm that actually produced the value"

let checkMatchRcScrutinee unit =
    unit
    |> (given (_) -> checkFixture("match_rc_scrutinee")(ExplainOwnership)("ownership"))
    |> (given (_) -> checkFixture("match_rc_scrutinee")(ExplainRc)("rc"))
    |> (given (_) -> checkFixture("match_rc_scrutinee")(ExplainReuse)("reuse"))
    |> (given (_) ->
        false
        |> scopedRewrite(["label"])(matchResultSlotJoinLine)
        |> rewriteLines
        |> checkKnownDifference("match_rc_scrutinee")(ExplainMemory)("memory")(matchResultSlotJoinReason))

// Scopes a line-rewrite to one or more `  representation [<label>]` sub-blocks, the same way
// `scopedRewrite` scopes to `Function: <name>` blocks: needed when a fixture's single function
// carries more than one representation block (a TCO loop's closure builder and its loop body),
// so a fix for one block does not touch the other.
let recursive isTargetRepresentationHeading (targetLabels: List(Str)) (line: Str) =
    match targetLabels with
        | [] -> false
        | label :: rest ->
            if line == "  representation [" + label + "]"
            then true
            else isTargetRepresentationHeading(rest)(line)

let recursive representationScopedRewrite (targetLabels: List(Str)) transform (inBlock: Bool) (lines: List(Str)) =
    match lines with
        | [] -> []
        | line :: rest ->
            if isRepresentationHeading(line)
            then
                line :: representationScopedRewrite(targetLabels)(transform)(isTargetRepresentationHeading(targetLabels)(line))(rest)
            else
                if inBlock
                then
                    rest
                    |> representationScopedRewrite(targetLabels)(transform)(inBlock)
                    |> append(transform(line))
                else line :: representationScopedRewrite(targetLabels)(transform)(inBlock)(rest)

// A TCO loop's own result-slot reload — read once at the loop's exit, after every iteration
// writes its candidate result to the same slot — hits the same last-write-wins post-hoc
// classification gap as an ordinary match's result join (see `matchResultSlotJoinReason`), and
// the closure builder that captures the loop's initial state carries a parallel gap of its own
// for the value copied into the environment record. Both are representation-only: ownership,
// perceus, and every other report kind for this fixture already match exactly.
let tcoLoopBodyReprLine (line: Str) =
    match line with
        | "    runtime rc:           1" -> []
        | "    copy value:           11" -> ["    copy value:           12"]
        | _ -> [line]

let tcoClosureBuilderReprLine (line: Str) =
    match line with
        | "    region:               3" -> ["    conservative unknown: 1", "    region:               1"]
        | "    copy value:           1" -> ["    copy value:           2"]
        | _ -> [line]

let tcoScalarLoopReprReason = "representation is classified by a post-hoc walk that is last-write-wins in program order rather than per-branch, so a TCO loop's own result-slot reload (like a match's result join) and its closure builder's environment-copied value are classified from whichever write reaches them last in program order instead of the value that actually produced them"

let tcoScalarLoopRepresentation (text: Str) =
    text
    |> rewriteLines(representationScopedRewrite(["lambda_1"])(tcoLoopBodyReprLine)(false))
    |> rewriteLines(representationScopedRewrite(["lambda_0"])(tcoClosureBuilderReprLine)(false))

let checkTcoScalarLoop unit =
    unit
    |> (given (_) -> checkFixture("tco_scalar_loop")(ExplainOwnership)("ownership"))
    |> (given (_) -> checkFixture("tco_scalar_loop")(ExplainRc)("rc"))
    |> (given (_) -> checkFixture("tco_scalar_loop")(ExplainReuse)("reuse"))
    |> (given (_) -> checkKnownDifference("tco_scalar_loop")(ExplainMemory)("memory")(tcoScalarLoopReprReason)(tcoScalarLoopRepresentation))

// The same TCO closure-builder gap as `tcoClosureBuilderReprLine`, plus the loop body's own
// owned `let` (a Str built fresh each iteration, compared, and dropped without escaping) adding a
// representation fact the post-hoc walk has no equivalent event for: one runtime-rc-classified
// value stage 0 records around that drop has no counterpart in this walk at all, so the total
// count itself is one short in addition to the last-write-wins reclassification.
let tcoOwnedLetLoopBodyReprLine (line: Str) =
    match line with
        | "    conservative unknown: 2" -> ["    conservative unknown: 1"]
        | "    region:               1" -> ["    region:               2"]
        | "    runtime rc:           2" -> []
        | "    copy value:           15" -> ["    copy value:           16"]
        | _ -> [line]

let tcoScalarOwnedLetReprReason = "representation is classified by a post-hoc walk that is last-write-wins in program order and has no event for the fact stage 0 records around an owned let's drop, so a TCO loop body with an owned local (built, compared, and dropped each iteration without escaping) is short one representation fact in addition to the usual result-slot join reclassification"

let tcoScalarOwnedLetRepresentation (text: Str) =
    text
    |> rewriteLines(representationScopedRewrite(["lambda_1"])(tcoOwnedLetLoopBodyReprLine)(false))
    |> rewriteLines(representationScopedRewrite(["lambda_0"])(tcoClosureBuilderReprLine)(false))

let checkTcoScalarOwnedLet unit =
    unit
    |> (given (_) -> checkFixture("tco_scalar_owned_let")(ExplainOwnership)("ownership"))
    |> (given (_) -> checkFixture("tco_scalar_owned_let")(ExplainRc)("rc"))
    |> (given (_) -> checkFixture("tco_scalar_owned_let")(ExplainReuse)("reuse"))
    |> (given (_) -> checkKnownDifference("tco_scalar_owned_let")(ExplainMemory)("memory")(tcoScalarOwnedLetReprReason)(tcoScalarOwnedLetRepresentation))

// The same TCO loop-join and closure-builder gaps as `tcoLoopBodyReprLine`/
// `tcoClosureBuilderReprLine`, with this fixture's own counts: the unused chained parameter
// changes which values the closure builder copies into the environment, spreading its single
// region fact across three categories instead of two.
let tcoUnusedChainLoopBodyReprLine (line: Str) =
    match line with
        | "    runtime rc:           1" -> []
        | "    copy value:           9" -> ["    copy value:           10"]
        | _ -> [line]

let tcoUnusedChainClosureBuilderReprLine (line: Str) =
    match line with
        | "    region:               3" -> ["    conservative unknown: 1", "    region:               1", "    copy value:           1"]
        | _ -> [line]

let tcoUnusedChainReprReason = "representation is classified by a post-hoc walk that is last-write-wins in program order rather than per-branch, so this TCO loop's result-slot join and its closure builder's environment-copied values are classified from whichever write reaches them last in program order instead of the value that actually produced them"

let tcoUnusedChainRepresentation (text: Str) =
    text
    |> rewriteLines(representationScopedRewrite(["lambda_1"])(tcoUnusedChainLoopBodyReprLine)(false))
    |> rewriteLines(representationScopedRewrite(["lambda_0"])(tcoUnusedChainClosureBuilderReprLine)(false))

let checkTcoUnusedChainParameter unit =
    unit
    |> (given (_) -> checkFixture("tco_unused_chain_parameter")(ExplainOwnership)("ownership"))
    |> (given (_) -> checkFixture("tco_unused_chain_parameter")(ExplainRc)("rc"))
    |> (given (_) -> checkFixture("tco_unused_chain_parameter")(ExplainReuse)("reuse"))
    |> (given (_) -> checkKnownDifference("tco_unused_chain_parameter")(ExplainMemory)("memory")(tcoUnusedChainReprReason)(tcoUnusedChainRepresentation))

let checkAllKinds (name: Str) =
    Unit
    |> (given (_) -> checkFixture(name)(ExplainOwnership)("ownership"))
    |> (given (_) -> checkFixture(name)(ExplainRc)("rc"))
    |> (given (_) -> checkFixture(name)(ExplainReuse)("reuse"))
    |> (given (_) -> checkFixture(name)(ExplainMemory)("memory"))

// The same last-write-wins representation gap as the TCO closure builders: the closure's own
// environment-copied value is classified from whichever branch wrote its slot last in program
// order rather than the branch that actually produced it.
let closureCaptureBuilderReprLine (line: Str) =
    match line with
        | "    region:               2" -> ["    region:               1"]
        | "    copy value:           1" -> ["    copy value:           2"]
        | _ -> [line]

let closureCaptureReprReason = "representation is classified by a post-hoc walk that is last-write-wins in program order rather than per-branch, so the closure builder's environment-copied value is classified from whichever branch wrote its slot last in program order instead of the branch that actually produced it"

let checkClosureCapture unit =
    unit
    |> (given (_) -> checkKnownDifference("closure_capture")(ExplainOwnership)("ownership")(curriedPartialApplicationAliasNotTraced)(yCapturedThroughPartialApplicationAliasNotUnique))
    |> (given (_) -> checkFixture("closure_capture")(ExplainRc)("rc"))
    |> (given (_) -> checkFixture("closure_capture")(ExplainReuse)("reuse"))
    |> (given (_) ->
        false
        |> representationScopedRewrite(["lambda_0"])(closureCaptureBuilderReprLine)
        |> rewriteLines
        |> checkKnownDifference("closure_capture")(ExplainMemory)("memory")(closureCaptureReprReason))

// Drops the named `representation [...]` blocks (heading and indented count lines) entirely,
// unlike `dropRepresentationBlocks` which drops every one: stage 0's own dispatch-wrapper
// allocation, absent because recursive-group lowering parity is not ported (see
// `recursiveGroupNotPorted`), so this port's report has no counterpart block for it at all.
let recursive dropNamedRepresentationBlocks (targetLabels: List(Str)) (dropping: Bool) (lines: List(Str)) =
    match lines with
        | [] -> []
        | line :: rest ->
            if isRepresentationHeading(line)
            then
                if isTargetRepresentationHeading(targetLabels)(line)
                then dropNamedRepresentationBlocks(targetLabels)(true)(rest)
                else line :: dropNamedRepresentationBlocks(targetLabels)(false)(rest)
            else
                if dropping && Ashes.Text.startsWith(line)("    ")
                then dropNamedRepresentationBlocks(targetLabels)(true)(rest)
                else line :: dropNamedRepresentationBlocks(targetLabels)(false)(rest)

let mutualRecursionMemory (text: Str) =
    rewriteLines(dropNamedRepresentationBlocks(["lambda_4", "lambda_5"])(false))(text)

let checkMutualRecursion unit =
    unit
    |> (given (_) -> checkFixture("mutual_recursion")(ExplainOwnership)("ownership"))
    |> (given (_) -> checkKnownDifference("mutual_recursion")(ExplainRc)("rc")(recursiveGroupNotPorted)(noRcOperations))
    |> (given (_) -> checkFixture("mutual_recursion")(ExplainReuse)("reuse"))
    |> (given (_) -> checkKnownDifference("mutual_recursion")(ExplainMemory)("memory")(recursiveGroupNotPorted)(mutualRecursionMemory))

let checkRecordPattern unit =
    unit
    |> (given (_) ->
        "p"
        |> componentReachNotTracked(["describe"])
        |> checkKnownDifference("record_pattern")(ExplainOwnership)("ownership")(componentReachNotTrackedReason))
    |> (given (_) -> checkFixture("record_pattern")(ExplainRc)("rc"))
    |> (given (_) -> checkFixture("record_pattern")(ExplainReuse)("reuse"))
    |> (given (_) ->
        ["describe"]
        |> componentReachNotTrackedInMemory
        |> checkKnownDifference("record_pattern")(ExplainMemory)("memory")(componentReachNotTrackedReason))

let checkTagGroupArmBrackets unit =
    unit
    |> (given (_) ->
        "token"
        |> componentReachNotTracked(["weight"])
        |> checkKnownDifference("tag_group_arm_brackets")(ExplainOwnership)("ownership")(componentReachNotTrackedReason))
    |> (given (_) -> checkFixture("tag_group_arm_brackets")(ExplainRc)("rc"))
    |> (given (_) -> checkFixture("tag_group_arm_brackets")(ExplainReuse)("reuse"))
    |> (given (_) ->
        ["weight"]
        |> componentReachNotTrackedInMemory
        |> checkKnownDifference("tag_group_arm_brackets")(ExplainMemory)("memory")(componentReachNotTrackedReason))

// The same last-write-wins representation gap as the TCO loop fixtures, at the scale these three
// functions' own arena-reset/RC-retain joins reach: each function's representation block moves
// several placements out of runtime-rc into conservative-unknown (and, for `prefixed`, out of
// region too) without changing the total, since a joined slot's classification comes from
// whichever branch wrote it last in program order rather than the branch that actually ran.
let aggregateChildrenRetainListedReprLine (line: Str) =
    match line with
        | "    conservative unknown: 2" -> ["    conservative unknown: 7"]
        | "    runtime rc:           12" -> ["    runtime rc:           4"]
        | _ -> [line]

let aggregateChildrenRetainPairReprLine (line: Str) =
    match line with
        | "    conservative unknown: 2" -> ["    conservative unknown: 6"]
        | "    runtime rc:           11" -> ["    runtime rc:           3"]
        | _ -> [line]

let aggregateChildrenRetainPrefixedReprLine (line: Str) =
    match line with
        | "    conservative unknown: 3" -> ["    conservative unknown: 5"]
        | "    region:               3" -> ["    region:               1"]
        | "    runtime rc:           9" -> ["    runtime rc:           4"]
        | _ -> [line]

let aggregateChildrenRetainReprReason = "representation is classified by a post-hoc walk that is last-write-wins in program order rather than per-branch, so these functions' own arena-reset/RC-retain joins are classified from whichever branch wrote a slot last in program order instead of the branch that actually produced the value"

let aggregateChildrenRetainRepresentation (text: Str) =
    text
    |> rewriteLines(representationScopedRewrite(["lambda_2"])(aggregateChildrenRetainListedReprLine)(false))
    |> rewriteLines(representationScopedRewrite(["lambda_1"])(aggregateChildrenRetainPairReprLine)(false))
    |> rewriteLines(representationScopedRewrite(["lambda_3"])(aggregateChildrenRetainPrefixedReprLine)(false))

let checkAggregateChildrenRetain unit =
    unit
    |> (given (_) ->
        "n"
        |> poisonNotPropagated(["pair", "listed", "prefixed"])
        |> checkKnownDifference("aggregate_children_retain")(ExplainOwnership)("ownership")(poisonNotPropagatedReason))
    |> (given (_) -> checkKnownDifference("aggregate_children_retain")(ExplainRc)("rc")(scalarEnvSpecializationNotCorrelated)(pairScalarEnvSpecializationLabeled))
    |> (given (_) -> checkFixture("aggregate_children_retain")(ExplainReuse)("reuse"))
    |> (given (_) -> checkKnownDifference("aggregate_children_retain")(ExplainMemory)("memory")(aggregateChildrenRetainReprReason)(aggregateChildrenRetainRepresentation))

let runExplainReportTests unit =
    unit
    |> (given (_) -> checkAllKinds("simple_arith"))
    |> (given (_) -> checkAllKinds("let_bindings"))
    |> (given (_) -> checkAllKinds("nested_let_scopes"))
    |> (given (_) -> checkAllKinds("scalar_match"))
    |> (given (_) -> checkAllKinds("ownerless_match"))
    |> (given (_) -> checkAllKinds("pattern_match"))
    |> (given (_) -> checkAllKinds("heap_result_builtin"))
    |> (given (_) -> checkAllKinds("heap_result_let"))
    |> (given (_) -> checkAllKinds("heap_result_list"))
    |> checkRecordPattern
    |> checkTagGroupArmBrackets
    |> (given (_) -> checkAllKinds("match_arm_copy_out"))
    |> (given (_) -> checkAllKinds("call_result_copy_out"))
    |> (given (_) -> checkAllKinds("call_argument_retain"))
    |> checkConsumedListArgument
    |> checkMatchRcScrutinee
    |> (given (_) -> checkAllKinds("match_list_scrutinee_drop"))
    |> checkTcoScalarLoop
    |> checkTcoScalarOwnedLet
    |> checkTcoUnusedChainParameter
    |> (given (_) -> checkAllKinds("owned_let_list_drop"))
    |> checkAggregateChildrenRetain
    |> checkClosureCapture
    |> checkMutualRecursion
    |> (given (_) -> Ashes.IO.print("all explain report tests passed"))
