// The TCO loop lowering of a self-recursive function, checked against stage 0: the affine
// self-append analysis that reserves a slot pair per accumulator at the loop entry, and the loop
// function's lowered IR text for the fixtures whose whole program does not yet match stage 0
// byte for byte (`selfhost/parity/semantics/lowered-ir`, read relative to the repository root
// like the backend suite's shared programs).
import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.TcoAffineAppend
export (
    value runTcoLoopLoweringTests,
)

let fixtureRoot = "selfhost/parity/semantics/lowered-ir"

let readFixture (path: Str) =
    match Ashes.IO.File.readText(path) with
        | Ok(value) -> value
        | Error(message) -> test.fail("could not read " + path + ": " + message)

let parsedProgram (source: Str) =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let loweredFixtureLines (name: Str) =
    (let source = readFixture(fixtureRoot + "/" + name + ".source")
    in
        match source
        |> parsedProgram
        |> lowerCoreProgramWithSource(name + ".ash")(source) with
            | CoreLoweringResult { program = Some(program), error = None } -> formatIr(program)(LoweredIr)(None)
            | CoreLoweringResult { error = Some(error) } -> test.fail("lowering failed for " + name + ": " + Ashes.Trait.Show.show(error))
            | _ -> test.fail("lowering produced no program for " + name))

let stageZeroFixtureLines (name: Str) =
    Ashes.Text.split(readFixture(fixtureRoot + "/" + name + ".ir"))("\n")

let isFunctionHeader (line: Str) = Ashes.Text.startsWith(line)("function ")

// The lines of the function whose header carries `originText`, up to the next header, trailing
// blank lines dropped.
let recursive functionBody (lines: List(Str)) (collected: List(Str)) =
    match lines with
        | [] -> Ashes.Collection.List.reverse(collected)
        | line :: rest ->
            if isFunctionHeader(line)
            then Ashes.Collection.List.reverse(collected)
            else functionBody(rest)(line :: collected)

let recursive dropTrailingBlank (reversed: List(Str)) =
    match reversed with
        | "" :: rest -> dropTrailingBlank(rest)
        | _ -> reversed

let recursive functionLines (originText: Str) (lines: List(Str)) =
    match lines with
        | [] -> test.fail("no function with origin " + originText)
        | line :: rest ->
            if isFunctionHeader(line) && Ashes.Text.contains(line)(originText)
            then
                []
                |> functionBody(rest)
                |> Ashes.Collection.List.reverse
                |> dropTrailingBlank
                |> Ashes.Collection.List.reverse
            else functionLines(originText)(rest)

let recursive withoutLinesContaining (fragment: Str) (lines: List(Str)) =
    match lines with
        | [] -> []
        | line :: rest ->
            if Ashes.Text.contains(line)(fragment)
            then withoutLinesContaining(fragment)(rest)
            else line :: withoutLinesContaining(fragment)(rest)

let expectSameLines (label: Str) (expected: List(Str)) (actual: List(Str)) =
    if expected == actual
    then Unit
    else
        test.fail(
            label + "\nexpected:\n" + Ashes.Text.join("\n")(expected) + "\nactual:\n" + Ashes.Text.join("\n")(actual)
        )

let selfCall (arguments: List(Expr)) =
    Ashes.Collection.List.foldLeft(given (callee) ->
        given (argument) -> ExprCall(callee)(argument)(false)(callArgumentsInline))(ExprVar("f"))(arguments)

// `if n == 0 then acc else f(n - 1)(acc + n)`: `acc` is its own leftmost-leaf append, `n` is
// consumed by the subtraction.
let expectAccumulatorAppendIsAffine unit =
    [ExprSubtract(ExprVar("n"))(ExprInt(1)), ExprAdd(ExprVar("acc"))(ExprVar("n"))]
    |> selfCall
    |> ExprIf(ExprEqual(ExprVar("n"))(ExprInt(0)))(ExprVar("acc"))
    |> affineSelfAppendOrdinals("f")(["n", "acc"])
    |> test.assertEqual([1])

// `f(rest)(count + 1)(total + x)` under a match on `xs`: both accumulators stay affine, the
// scrutinee mention disqualifies `xs`.
let expectMatchScrutineeDisqualifies unit =
    None
    |> ExprMatch(ExprVar("xs"))([(PatternWildcard, ExprInt(0), None), (PatternCons(PatternVar("x"))(PatternVar("rest")), selfCall([ExprVar("rest"), ExprAdd(ExprVar("count"))(ExprInt(1)), ExprAdd(ExprVar("total"))(ExprVar("x"))]), None)])
    |> affineSelfAppendOrdinals("f")(["xs", "count", "total"])
    |> test.assertEqual([1, 2])

// An argument that is not the accumulator's own append (`if c then acc + 1 else acc`) consumes it.
let expectConditionalArgumentIsNotAffine unit =
    [ExprSubtract(ExprVar("n"))(ExprInt(1)), ExprIf(ExprVar("c"))(ExprAdd(ExprVar("acc"))(ExprInt(1)))(ExprVar("acc"))]
    |> selfCall
    |> affineSelfAppendOrdinals("f")(["n", "acc"])
    |> test.assertEqual([])

// `let acc2 = acc + n in f(n - 1)(acc2)`: the single-use alias carries the append.
let expectSingleUseAliasKeepsAffinity unit =
    []
    |> ExprLet("acc2")(ExprAdd(ExprVar("acc"))(ExprVar("n")))(selfCall([ExprSubtract(ExprVar("n"))(ExprInt(1)), ExprVar("acc2")]))([])(None)
    |> affineSelfAppendOrdinals("f")(["n", "acc"])
    |> test.assertEqual([1])

// A parameter the body never mentions on a continuing path stays affine, as in stage 0.
let expectUnmentionedParameterStaysAffine unit =
    [ExprInt(0), ExprSubtract(ExprVar("acc"))(ExprInt(1))]
    |> selfCall
    |> ExprIf(ExprEqual(ExprVar("acc"))(ExprInt(0)))(ExprInt(0))
    |> affineSelfAppendOrdinals("f")(["n", "acc"])
    |> test.assertEqual([0])

// A body without an exact self-call has no affine parameters.
let expectNoSelfCallMeansNoAffinity unit =
    ExprInt(1)
    |> ExprAdd(ExprVar("acc"))
    |> affineSelfAppendOrdinals("f")(["n", "acc"])
    |> test.assertEqual([])

// The list walk's loop function matches stage 0 line for line: the runtime-managed list
// parameter's back edge stores the borrowed tail, releases the pattern owner, and skips the arena
// reset. The program as a whole still lacks the closure environment normalizer and dropper for
// the list-typed capture, which keeps the fixture out of the parity runner.
let expectListWalkLoopFunctionMatchesStageZero unit =
    "tco_list_walk"
    |> loweredFixtureLines
    |> functionLines("[ClosureHelper from walk]")
    |> expectSameLines("list walk loop function")("tco_list_walk"
    |> stageZeroFixtureLines
    |> functionLines("[ClosureHelper from walk]"))

// The operator-operand program's scalar loops (`countLeft`, `countRight`) match stage 0 except
// for the arena reset closing the window of the non-tail self-call under the operator: stage 0
// infers the binding's result type before lowering, the selfhost's single-file lowering still
// sees a type variable there. The list-walking loops of the same program differ by their
// runtime-managed parameters and stay out of the comparison.
let expectScalarOperatorOperandLoopMatchesStageZero (originText: Str) (windowClose: Str) (windowReclaim: Str) (lines: List(Str)) (expected: List(Str)) =
    lines
    |> functionLines(originText)
    |> expectSameLines(originText + " loop function")(expected
    |> functionLines(originText)
    |> withoutLinesContaining(windowClose)
    |> withoutLinesContaining(windowReclaim))

let expectOperatorOperandLoopsMatchStageZero unit =
    (let lines = loweredFixtureLines("tco_non_tail_self_call_in_operator_operand")
    in
        let expected = stageZeroFixtureLines("tco_non_tail_self_call_in_operator_operand")
        in
            Unit
            |> (given (_) -> expectScalarOperatorOperandLoopMatchesStageZero("[SourceFunction from countLeft]")("RestoreArenaState     CursorLocalSlot=10 EndLocalSlot=11 PreRestoreEndSlot=12")("ReclaimArenaChunks    SavedEndSlot=11 PreRestoreEndSlot=12")(lines)(expected))
            |> (given (_) -> expectScalarOperatorOperandLoopMatchesStageZero("[SourceFunction from countRight]")("RestoreArenaState     CursorLocalSlot=10 EndLocalSlot=11 PreRestoreEndSlot=12")("ReclaimArenaChunks    SavedEndSlot=11 PreRestoreEndSlot=12")(lines)(expected)))

let runTcoLoopLoweringTests unit =
    unit
    |> expectAccumulatorAppendIsAffine
    |> (given (_) -> expectMatchScrutineeDisqualifies(Unit))
    |> (given (_) -> expectConditionalArgumentIsNotAffine(Unit))
    |> (given (_) -> expectSingleUseAliasKeepsAffinity(Unit))
    |> (given (_) -> expectUnmentionedParameterStaysAffine(Unit))
    |> (given (_) -> expectNoSelfCallMeansNoAffinity(Unit))
    |> (given (_) -> expectListWalkLoopFunctionMatchesStageZero(Unit))
    |> (given (_) -> expectOperatorOperandLoopsMatchStageZero(Unit))
    |> (given (_) -> Ashes.IO.print("all self-hosted tco loop lowering tests passed"))
