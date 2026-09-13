// The TCO loop lowering of a self-recursive function, checked against stage 0: the affine
// self-append analysis that reserves a slot pair per accumulator at the loop entry, and the loop
// function's lowered IR text for the fixtures whose whole program does not yet match stage 0
// byte for byte.
import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.TcoAffineAppend
import LoweredIrFixtures.loweredFixtureLines
import LoweredIrFixtures.stageZeroFixtureLines
import LoweredIrFixtures.functionLines
import LoweredIrFixtures.withFunctionLocalLabels
import LoweredIrFixtures.expectSameLines
export (
    value runTcoLoopLoweringTests,
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
// reset. The whole program, with the closure environment normalizers and dropper of the
// list-typed capture, is pinned by the parity runner.
let expectListWalkLoopFunctionMatchesStageZero unit =
    "tco_list_walk"
    |> loweredFixtureLines
    |> functionLines("[ClosureHelper from walk]")
    |> expectSameLines("list walk loop function")("tco_list_walk"
    |> stageZeroFixtureLines
    |> functionLines("[ClosureHelper from walk]"))

// The operator-operand program's `sumTo` (`n + sumTo(n - 1)`) matches the stage-0 compiler's
// own dump of the fixture (`ashes compile --emit-ir lowered`) line for line: the compiler
// stitches `Ashes.Trait` in, so the operator records a trait requirement and the binding is
// lowered against its closed inferred type, result included, and the non-tail self call under
// the operator is a plain call whose window is reset after it. This lowering reaches the same
// shape by lowering the body again once its first lowering closed the binding's arrow. The
// committed oracle fixture lowers without trait declarations and so defers the call's result
// instead; the other five loops of the program match the compiler's dump the same way and are
// left to the whole-program comparison once the oracle lowers with the trait declarations.
let compiledSumToLoop =
    [
        "  locals=6 temps=13",
        "    LoadLocal             Target=0 Slot=1   (tco_non_tail_self_call_in_operator_operand.ash:34:8)",
        "    LoadConstInt          Target=1 Value=0   (tco_non_tail_self_call_in_operator_operand.ash:34:13)",
        "    CmpIntEq              Target=2 Left=0 Right=1   (tco_non_tail_self_call_in_operator_operand.ash:34:8)",
        "    JumpIfFalse           CondTemp=2 Target=else_0   (tco_non_tail_self_call_in_operator_operand.ash:34:5)",
        "    LoadConstInt          Target=3 Value=0   (tco_non_tail_self_call_in_operator_operand.ash:35:10)",
        "    StoreLocal            Slot=2 Source=3   (tco_non_tail_self_call_in_operator_operand.ash:34:5)",
        "    Jump                  Target=endif_1   (tco_non_tail_self_call_in_operator_operand.ash:34:5)",
        "  else_0:",
        "    LoadLocal             Target=4 Slot=1   (tco_non_tail_self_call_in_operator_operand.ash:36:10)",
        "    SaveArenaState        CursorLocalSlot=3 EndLocalSlot=4",
        "    LoadLocal             Target=6 Slot=0   (tco_non_tail_self_call_in_operator_operand.ash:36:14)",
        "    MakeClosure           Target=5 FuncLabel=lambda_3 EnvPtrTemp=6 EnvSizeBytes=0   (tco_non_tail_self_call_in_operator_operand.ash:36:14)",
        "    LoadLocal             Target=7 Slot=1   (tco_non_tail_self_call_in_operator_operand.ash:36:20)",
        "    LoadConstInt          Target=8 Value=1   (tco_non_tail_self_call_in_operator_operand.ash:36:24)",
        "    SubInt                Target=9 Left=7 Right=8   (tco_non_tail_self_call_in_operator_operand.ash:36:20)",
        "    CallClosure           Target=10 ClosureTemp=5 ArgTemp=9   (tco_non_tail_self_call_in_operator_operand.ash:36:14)",
        "    RestoreArenaState     CursorLocalSlot=3 EndLocalSlot=4 PreRestoreEndSlot=5",
        "    ReclaimArenaChunks    SavedEndSlot=4 PreRestoreEndSlot=5",
        "    AddInt                Target=11 Left=4 Right=10   (tco_non_tail_self_call_in_operator_operand.ash:36:10)",
        "    StoreLocal            Slot=2 Source=11   (tco_non_tail_self_call_in_operator_operand.ash:34:5)",
        "  endif_1:",
        "    LoadLocal             Target=12 Slot=2   (tco_non_tail_self_call_in_operator_operand.ash:34:5)",
        "    Return                Source=12   (tco_non_tail_self_call_in_operator_operand.ash:33:1)"
    ]

let expectOperandSelfCallLoopMatchesCompiler unit =
    "tco_non_tail_self_call_in_operator_operand"
    |> loweredFixtureLines
    |> functionLines("[SourceFunction from sumTo]")
    |> withFunctionLocalLabels
    |> expectSameLines("[SourceFunction from sumTo] loop function")(compiledSumToLoop)

let runTcoLoopLoweringTests unit =
    unit
    |> expectAccumulatorAppendIsAffine
    |> (given (_) -> expectMatchScrutineeDisqualifies(Unit))
    |> (given (_) -> expectConditionalArgumentIsNotAffine(Unit))
    |> (given (_) -> expectSingleUseAliasKeepsAffinity(Unit))
    |> (given (_) -> expectUnmentionedParameterStaysAffine(Unit))
    |> (given (_) -> expectNoSelfCallMeansNoAffinity(Unit))
    |> (given (_) -> expectListWalkLoopFunctionMatchesStageZero(Unit))
    |> (given (_) -> expectOperandSelfCallLoopMatchesCompiler(Unit))
    |> (given (_) -> Ashes.IO.print("all self-hosted tco loop lowering tests passed"))
