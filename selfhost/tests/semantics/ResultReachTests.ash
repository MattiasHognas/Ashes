// Unit tests for the self-hosted result-reach walk and the entry normalization it drives: which
// body shapes forward a variable to every result, and the `rc_arg_normalize` entry block plus the
// `AcceptsRuntimeManagedArgument` closure flag a string or record parameter reaching the result
// lowers to, against stage 0's instruction text.

import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.ResultReach
export (
    value runResultReachTests,
)

let parsedExpression source =
    match parseExpression(source) with
        | ExpressionParseResult { expression = expression, diagnostics = [] } -> expression
        | ExpressionParseResult { diagnostics = diagnostics } -> test.fail("expression should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let reaches constructors callees source variable =
    resultAlwaysReachesVariable(constructors)(callees)(parsedExpression(source))(variable)

let plainReaches source variable = reaches([])([])(source)(variable)

let wrapCallee unit = [("wrap", ["s"], parsedExpression("[s]"))]

let testVariableItselfReaches unit =
    "s"
    |> plainReaches("s")
    |> test.assertEqual(true)

let testOtherVariableDoesNotReach unit =
    "s"
    |> plainReaches("t")
    |> test.assertEqual(false)

let testListLiteralReaches unit =
    "s"
    |> plainReaches("[1, s]")
    |> test.assertEqual(true)

let testTupleReaches unit =
    "s"
    |> plainReaches("(s, 1)")
    |> test.assertEqual(true)

let testConsReachesThroughTail unit =
    "s"
    |> plainReaches("1 :: s")
    |> test.assertEqual(true)

let testRecordFieldReaches unit =
    "s"
    |> plainReaches("Pair(label = s, count = 1)")
    |> test.assertEqual(true)

let testRecordUpdateTargetReaches unit =
    "s"
    |> plainReaches("s with count = 2")
    |> test.assertEqual(true)

let testRecordUpdateFieldReaches unit =
    "s"
    |> plainReaches("p with label = s")
    |> test.assertEqual(true)

let testIfReachesOnBothBranches unit =
    "s"
    |> plainReaches("if c then [s] else s :: []")
    |> test.assertEqual(true)

let testIfMissingOneBranchDoesNotReach unit =
    "s"
    |> plainReaches("if c then [s] else []")
    |> test.assertEqual(false)

let testMatchReachesOnEveryArm unit =
    "s"
    |> plainReaches("match n with | 0 -> [s] | _ -> (s, 1)")
    |> test.assertEqual(true)

let testMatchMissingOneArmDoesNotReach unit =
    "s"
    |> plainReaches("match n with | 0 -> [s] | _ -> []")
    |> test.assertEqual(false)

let testMatchArmRebindingDoesNotReach unit =
    "s"
    |> plainReaches("match p with | Pair { label = s } -> s")
    |> test.assertEqual(false)

let testLambdaBodyReaches unit =
    "s"
    |> plainReaches("given (t) -> [s]")
    |> test.assertEqual(true)

let testLambdaShadowingDoesNotReach unit =
    "s"
    |> plainReaches("given (s) -> [s]")
    |> test.assertEqual(false)

let testLetBodyDoesNotReach unit =
    "s"
    |> plainReaches("let t = s in t")
    |> test.assertEqual(false)

let testOperatorDoesNotReach unit =
    "s"
    |> plainReaches("s + \"x\"")
    |> test.assertEqual(false)

let testConstructorApplicationReaches unit =
    "s"
    |> reaches(["Some"])([])("Some(s)")
    |> test.assertEqual(true)

let testUnknownCalleeDoesNotReach unit =
    "s"
    |> reaches([])([])("Some(s)")
    |> test.assertEqual(false)

let testKnownCalleeReachesThroughItsParameter unit =
    "s"
    |> reaches([])(wrapCallee(Unit))("wrap(s)")
    |> test.assertEqual(true)

let testKnownCalleeWithOtherArgumentDoesNotReach unit =
    "s"
    |> reaches([])(wrapCallee(Unit))("wrap(1)")
    |> test.assertEqual(false)

let testOverAppliedKnownCalleeDoesNotReach unit =
    "s"
    |> reaches([])(wrapCallee(Unit))("wrap(s)(1)")
    |> test.assertEqual(false)

// A callee forwarding the argument to itself never bottoms out; the walk gives up at its depth
// bound instead of looping.
let testSelfForwardingCalleeStopsAtDepthBound unit =
    "s"
    |> reaches([])([("loop", ["s"], parsedExpression("loop(s)"))])("loop(s)")
    |> test.assertEqual(false)

let parsedProgram source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let loweredLines name source =
    match source
    |> parsedProgram
    |> lowerCoreProgramWithSource(name)(source) with
        | CoreLoweringResult { program = Some(lowered), error = None } -> formatIr(lowered)(LoweredIr)(None)
        | CoreLoweringResult { error = Some(error) } -> test.fail("program lowering failed: " + Ashes.Trait.Show.show(error))
        | _ -> test.fail("program lowering produced no program")

let recursive dropUntilPrefix (prefix: Str) (lines: List(Str)) =
    match lines with
        | [] -> []
        | line :: rest ->
            if Ashes.Text.startsWith(line)(prefix)
            then line :: rest
            else dropUntilPrefix(prefix)(rest)

let recursive takeUntilBlank (lines: List(Str)) =
    match lines with
        | [] -> []
        | line :: rest ->
            if line == ""
            then []
            else line :: takeUntilBlank(rest)

// The text of one lifted function in the formatted IR: its header through its last instruction.
let functionText (label: Str) (lines: List(Str)) =
    lines
    |> dropUntilPrefix("function " + label)
    |> takeUntilBlank
    |> Ashes.Text.join("\n")

let recursive anyLineContains (needle: Str) (lines: List(Str)) =
    match lines with
        | [] -> false
        | line :: rest -> Ashes.Text.contains(line)(needle) || anyLineContains(needle)(rest)

let stringParameterSource unit = "let wrap (s: Str) = [s]\n\nmatch wrap(Ashes.Text.fromInt(7)) with\n    | x :: [] -> Ashes.IO.print(x)\n    | _ -> Ashes.IO.print(\"other\")\n"

let stringParameterFunction unit =
    Ashes.Text.join("\n")([
        "function lambda_0  [SourceFunction from wrap]",
        "  locals=3 temps=7",
        "    LoadLocal             Target=3 Slot=1",
        "    LoadArgumentOwnership Target=4",
        "    JumpIfFalse           CondTemp=4 Target=rc_arg_normalize_copy_0",
        "    StoreLocal            Slot=2 Source=3",
        "    Jump                  Target=rc_arg_normalize_done_1",
        "  rc_arg_normalize_copy_0:",
        "    CopyOutArena          DestTemp=5 SrcTemp=3 RuntimeManaged=true Purpose=RcNormalization",
        "    StoreLocal            Slot=2 Source=5",
        "  rc_arg_normalize_done_1:",
        "    LoadLocal             Target=6 Slot=2",
        "    StoreLocal            Slot=1 Source=6",
        "    LoadConstInt          Target=0 Value=0   (parameter_reaches_result_string.ash:1:21)",
        "    LoadLocal             Target=1 Slot=1   (parameter_reaches_result_string.ash:1:22)",
        "    Alloc                 Target=2 SizeBytes=16   (parameter_reaches_result_string.ash:1:21)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=0 Source=1   (parameter_reaches_result_string.ash:1:21)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=8 Source=0   (parameter_reaches_result_string.ash:1:21)",
        "    Return                Source=2"
    ])

// A string parameter reaching the result is normalized at entry: the hidden ownership flag
// selects the borrowed-argument copy, and the owned value is stored back into the argument slot.
let testStringParameterEntryNormalization unit =
    Unit
    |> stringParameterSource
    |> loweredLines("parameter_reaches_result_string.ash")
    |> functionText("lambda_0")
    |> test.assertEqual(stringParameterFunction(Unit))

// The closure carrying the normalized function advertises that it accepts a runtime-managed
// argument.
let testStringParameterClosureFlag unit =
    Unit
    |> stringParameterSource
    |> loweredLines("parameter_reaches_result_string.ash")
    |> anyLineContains("MakeClosureStack      Target=1 FuncLabel=lambda_0 EnvPtrTemp=0 EnvSizeBytes=0 AcceptsRuntimeManagedArgument=true")
    |> test.assertEqual(true)

let recordParameterSource unit = "type Pair =\n    | label: Str\n    | count: Int\n\nlet wrap (pair: Pair) = [pair]\n\nmatch wrap(Pair(label = Ashes.Text.fromInt(3), count = 1)) with\n    | Pair { label = label } :: [] -> Ashes.IO.print(label)\n    | _ -> Ashes.IO.print(\"other\")\n"

let recordParameterFunction unit =
    Ashes.Text.join("\n")([
        "function lambda_0  [SourceFunction from wrap]",
        "  locals=3 temps=11",
        "    LoadLocal             Target=3 Slot=1",
        "    LoadArgumentOwnership Target=4",
        "    JumpIfFalse           CondTemp=4 Target=rc_arg_normalize_copy_0",
        "    StoreLocal            Slot=2 Source=3",
        "    Jump                  Target=rc_arg_normalize_done_1",
        "  rc_arg_normalize_copy_0:",
        "    CopyOutArena          DestTemp=7 SrcTemp=3 StaticSizeBytes=16 RuntimeManaged=true Purpose=RcNormalization",
        "    GetAdtField           Target=8 Ptr=3 FieldIndex=0 Tagless=true",
        "    CopyOutArena          DestTemp=9 SrcTemp=8 RuntimeManaged=true Purpose=RcNormalization",
        "    SetAdtField           Ptr=7 FieldIndex=0 Source=9 Tagless=true",
        "    StoreLocal            Slot=2 Source=7",
        "  rc_arg_normalize_done_1:",
        "    LoadLocal             Target=10 Slot=2",
        "    StoreLocal            Slot=1 Source=10",
        "    LoadConstInt          Target=0 Value=0   (parameter_reaches_result_record.ash:5:25)",
        "    LoadLocal             Target=1 Slot=1   (parameter_reaches_result_record.ash:5:26)",
        "    Alloc                 Target=2 SizeBytes=16   (parameter_reaches_result_record.ash:5:25)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=0 Source=1   (parameter_reaches_result_record.ash:5:25)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=8 Source=0   (parameter_reaches_result_record.ash:5:25)",
        "    Return                Source=2"
    ])

// A record parameter with a string field is deep-copied on the borrowed path: the cell is copied
// out whole, then the string field is copied out and stored into the copy.
let testRecordParameterEntryNormalization unit =
    Unit
    |> recordParameterSource
    |> loweredLines("parameter_reaches_result_record.ash")
    |> functionText("lambda_0")
    |> test.assertEqual(recordParameterFunction(Unit))

let scalarParameterFunction unit =
    Ashes.Text.join("\n")([
        "function lambda_0  [SourceFunction from keep]",
        "  locals=2 temps=3",
        "    LoadConstInt          Target=0 Value=0   (scalar_parameter.ash:1:21)",
        "    LoadLocal             Target=1 Slot=1   (scalar_parameter.ash:1:22)",
        "    Alloc                 Target=2 SizeBytes=16 RuntimeManaged=true   (scalar_parameter.ash:1:21)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=0 Source=1   (scalar_parameter.ash:1:21)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=8 Source=0   (scalar_parameter.ash:1:21)",
        "    Return                Source=2"
    ])

// A scalar parameter reaching the result is left alone: there is nothing to own.
let testScalarParameterIsNotNormalized unit =
    "let keep (n: Int) = [n]\n\nmatch keep(7) with\n    | x :: [] -> Ashes.IO.print(Ashes.Text.fromInt(x))\n    | _ -> Ashes.IO.print(\"other\")\n"
    |> loweredLines("scalar_parameter.ash")
    |> functionText("lambda_0")
    |> test.assertEqual(scalarParameterFunction(Unit))

let runResultReachTests unit =
    unit
    |> testVariableItselfReaches
    |> testOtherVariableDoesNotReach
    |> testListLiteralReaches
    |> testTupleReaches
    |> testConsReachesThroughTail
    |> testRecordFieldReaches
    |> testRecordUpdateTargetReaches
    |> testRecordUpdateFieldReaches
    |> testIfReachesOnBothBranches
    |> testIfMissingOneBranchDoesNotReach
    |> testMatchReachesOnEveryArm
    |> testMatchMissingOneArmDoesNotReach
    |> testMatchArmRebindingDoesNotReach
    |> testLambdaBodyReaches
    |> testLambdaShadowingDoesNotReach
    |> testLetBodyDoesNotReach
    |> testOperatorDoesNotReach
    |> testConstructorApplicationReaches
    |> testUnknownCalleeDoesNotReach
    |> testKnownCalleeReachesThroughItsParameter
    |> testKnownCalleeWithOtherArgumentDoesNotReach
    |> testOverAppliedKnownCalleeDoesNotReach
    |> testSelfForwardingCalleeStopsAtDepthBound
    |> testStringParameterEntryNormalization
    |> testStringParameterClosureFlag
    |> testRecordParameterEntryNormalization
    |> testScalarParameterIsNotNormalized
