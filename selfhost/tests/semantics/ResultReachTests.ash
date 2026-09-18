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

let testLetBodyReachingTheVariableReaches unit =
    "s"
    |> plainReaches("let t = 1 in [s]")
    |> test.assertEqual(true)

let testLetShadowingDoesNotReach unit =
    "s"
    |> plainReaches("let s = 1 in s")
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
        "  locals=4 temps=12",
        "    LoadLocal             Target=3 Slot=1   (parameter_reaches_result_string.ash:1:1)",
        "    LoadArgumentOwnership Target=4   (parameter_reaches_result_string.ash:1:1)",
        "    LoadConstInt          Target=5 Value=1   (parameter_reaches_result_string.ash:1:1)",
        "    AndInt                Target=6 Left=4 Right=5   (parameter_reaches_result_string.ash:1:1)",
        "    JumpIfFalse           CondTemp=6 Target=rc_arg_normalize_copy_0   (parameter_reaches_result_string.ash:1:1)",
        "    StoreLocal            Slot=2 Source=3   (parameter_reaches_result_string.ash:1:1)",
        "    Jump                  Target=rc_arg_normalize_done_1   (parameter_reaches_result_string.ash:1:1)",
        "  rc_arg_normalize_copy_0:",
        "    IsReferenceCounted    Target=7 SourceTemp=3",
        "    JumpIfFalse           CondTemp=7 Target=rc_representation_copy_2   (parameter_reaches_result_string.ash:1:1)",
        "    RcDup                 Target=8 SourceTemp=3 RuntimeManaged=true",
        "    StoreLocal            Slot=3 Source=8   (parameter_reaches_result_string.ash:1:1)",
        "    Jump                  Target=rc_representation_done_3   (parameter_reaches_result_string.ash:1:1)",
        "  rc_representation_copy_2:",
        "    CopyOutArena          DestTemp=9 SrcTemp=3 RuntimeManaged=true Purpose=RcNormalization",
        "    StoreLocal            Slot=3 Source=9   (parameter_reaches_result_string.ash:1:1)",
        "  rc_representation_done_3:",
        "    LoadLocal             Target=10 Slot=3   (parameter_reaches_result_string.ash:1:1)",
        "    StoreLocal            Slot=2 Source=10   (parameter_reaches_result_string.ash:1:1)",
        "  rc_arg_normalize_done_1:",
        "    LoadLocal             Target=11 Slot=2   (parameter_reaches_result_string.ash:1:1)",
        "    StoreLocal            Slot=1 Source=11   (parameter_reaches_result_string.ash:1:1)",
        "    LoadConstInt          Target=0 Value=0   (parameter_reaches_result_string.ash:1:21)",
        "    LoadLocal             Target=1 Slot=1   (parameter_reaches_result_string.ash:1:22)",
        "    Alloc                 Target=2 SizeBytes=16   (parameter_reaches_result_string.ash:1:21)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=0 Source=1   (parameter_reaches_result_string.ash:1:21)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=8 Source=0   (parameter_reaches_result_string.ash:1:21)",
        "    Return                Source=2   (parameter_reaches_result_string.ash:1:1)"
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
        "  locals=5 temps=19",
        "    LoadLocal             Target=3 Slot=1   (parameter_reaches_result_record.ash:5:1)",
        "    LoadArgumentOwnership Target=4   (parameter_reaches_result_record.ash:5:1)",
        "    LoadConstInt          Target=5 Value=1   (parameter_reaches_result_record.ash:5:1)",
        "    AndInt                Target=6 Left=4 Right=5   (parameter_reaches_result_record.ash:5:1)",
        "    JumpIfFalse           CondTemp=6 Target=rc_arg_normalize_copy_0   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=2 Source=3   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_arg_normalize_done_1   (parameter_reaches_result_record.ash:5:1)",
        "  rc_arg_normalize_copy_0:",
        "    IsReferenceCounted    Target=7 SourceTemp=3",
        "    JumpIfFalse           CondTemp=7 Target=rc_representation_copy_2   (parameter_reaches_result_record.ash:5:1)",
        "    RcDup                 Target=8 SourceTemp=3 RuntimeManaged=true",
        "    StoreLocal            Slot=3 Source=8   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_representation_done_3   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_copy_2:",
        "    CopyOutArena          DestTemp=11 SrcTemp=3 StaticSizeBytes=16 RuntimeManaged=true Purpose=RcNormalization",
        "    GetAdtField           Target=12 Ptr=3 FieldIndex=0 Tagless=true   (parameter_reaches_result_record.ash:5:1)",
        "    IsReferenceCounted    Target=13 SourceTemp=12",
        "    JumpIfFalse           CondTemp=13 Target=rc_representation_copy_4   (parameter_reaches_result_record.ash:5:1)",
        "    RcDup                 Target=14 SourceTemp=12 RuntimeManaged=true",
        "    StoreLocal            Slot=4 Source=14   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_representation_done_5   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_copy_4:",
        "    CopyOutArena          DestTemp=15 SrcTemp=12 RuntimeManaged=true Purpose=RcNormalization",
        "    StoreLocal            Slot=4 Source=15   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_done_5:",
        "    LoadLocal             Target=16 Slot=4   (parameter_reaches_result_record.ash:5:1)",
        "    SetAdtField           Ptr=11 FieldIndex=0 Source=16 Tagless=true   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=3 Source=11   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_done_3:",
        "    LoadLocal             Target=17 Slot=3   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=2 Source=17   (parameter_reaches_result_record.ash:5:1)",
        "  rc_arg_normalize_done_1:",
        "    LoadLocal             Target=18 Slot=2   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=1 Source=18   (parameter_reaches_result_record.ash:5:1)",
        "    LoadConstInt          Target=0 Value=0   (parameter_reaches_result_record.ash:5:25)",
        "    LoadLocal             Target=1 Slot=1   (parameter_reaches_result_record.ash:5:26)",
        "    Alloc                 Target=2 SizeBytes=16   (parameter_reaches_result_record.ash:5:25)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=0 Source=1   (parameter_reaches_result_record.ash:5:25)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=8 Source=0   (parameter_reaches_result_record.ash:5:25)",
        "    Return                Source=2   (parameter_reaches_result_record.ash:5:1)"
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
        "  locals=4 temps=13",
        "    LoadConstInt          Target=0 Value=0   (scalar_parameter.ash:1:21)",
        "    LoadLocal             Target=1 Slot=1   (scalar_parameter.ash:1:22)",
        "    Alloc                 Target=2 SizeBytes=16 RuntimeManaged=true   (scalar_parameter.ash:1:21)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=0 Source=1   (scalar_parameter.ash:1:21)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=8 Source=0   (scalar_parameter.ash:1:21)",
        "    LoadArgumentOwnership Target=3   (scalar_parameter.ash:1:1)",
        "    LoadConstInt          Target=4 Value=1   (scalar_parameter.ash:1:1)",
        "    ShrInt                Target=5 Left=3 Right=4   (scalar_parameter.ash:1:1)",
        "    JumpIfFalse           CondTemp=5 Target=rc_result_owned_0   (scalar_parameter.ash:1:1)",
        "    CopyOutList           DestTemp=6 SrcTemp=2 HeadCopy=Inline Purpose=ArenaResultBoundary   (scalar_parameter.ash:1:1)",
        "    StoreLocal            Slot=3 Source=2   (scalar_parameter.ash:1:1)",
        "  rcdrop_list_2:",
        "    LoadLocal             Target=7 Slot=3   (scalar_parameter.ash:1:1)",
        "    LoadConstInt          Target=8 Value=0   (scalar_parameter.ash:1:1)",
        "    CmpIntNe              Target=9 Left=7 Right=8   (scalar_parameter.ash:1:1)",
        "    JumpIfFalse           CondTemp=9 Target=rcdrop_list_end_4   (scalar_parameter.ash:1:1)",
        "    RcIsUnique            Target=10 SourceTemp=7",
        "    JumpIfFalse           CondTemp=10 Target=rcdrop_list_shared_3   (scalar_parameter.ash:1:1)",
        "    LoadMemOffset         Target=11 BasePtr=7 OffsetBytes=8   (scalar_parameter.ash:1:1)",
        "    RcDrop                SourceTemp=7 TypeName=List RuntimeManaged=true",
        "    StoreLocal            Slot=3 Source=11   (scalar_parameter.ash:1:1)",
        "    Jump                  Target=rcdrop_list_2   (scalar_parameter.ash:1:1)",
        "  rcdrop_list_shared_3:",
        "    RcDrop                SourceTemp=7 TypeName=List RuntimeManaged=true",
        "    Jump                  Target=rcdrop_list_end_4   (scalar_parameter.ash:1:1)",
        "  rcdrop_list_end_4:",
        "    StoreLocal            Slot=2 Source=6   (scalar_parameter.ash:1:1)",
        "    Jump                  Target=rc_result_done_1   (scalar_parameter.ash:1:1)",
        "  rc_result_owned_0:",
        "    StoreLocal            Slot=2 Source=2   (scalar_parameter.ash:1:1)",
        "  rc_result_done_1:",
        "    LoadLocal             Target=12 Slot=2   (scalar_parameter.ash:1:1)",
        "    Return                Source=12   (scalar_parameter.ash:1:1)"
    ])

// A scalar parameter reaching the result is left alone: there is nothing to own. The fresh
// reference-counted list the body returns still carries the arena-result boundary in its
// epilogue, located at the binding's own declaration like every instruction the binding's
// finalization emits.
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
    |> testLetBodyReachingTheVariableReaches
    |> testLetShadowingDoesNotReach
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
