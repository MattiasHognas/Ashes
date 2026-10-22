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

// The callee table as the walk consults it: each known function's parameter chain and the
// parameters its result always reaches, as the reach analysis's must-reach table answers them.
let recursive calleeTable (callees: List((Str, List(Str), List(Str)))) (name: Str) =
    match callees with
        | [] -> None
        | (candidate, parameters, reached) :: rest ->
            if candidate == name
            then Some((parameters, reached))
            else calleeTable(rest)(name)

let reaches constructors callees source variable =
    resultAlwaysReachesVariable(constructors)(calleeTable(callees))(parsedExpression(source))(variable)

let plainReaches source variable = reaches([])([])(source)(variable)

let wrapCallee unit = [("wrap", ["s"], ["s"])]

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

// A callee whose result the must-reach table does not answer for the parameter does not forward it.
let testCalleeNotReachingItsParameterDoesNotReach unit =
    "s"
    |> reaches([])([("loop", ["s"], [])])("loop(s)")
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
        "  locals=2 temps=3",
        "    LoadConstInt          Target=0 Value=0   (parameter_reaches_result_string.ash:1:21)",
        "    LoadLocal             Target=1 Slot=1   (parameter_reaches_result_string.ash:1:22)",
        "    Alloc                 Target=2 SizeBytes=16   (parameter_reaches_result_string.ash:1:21)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=0 Source=1   (parameter_reaches_result_string.ash:1:21)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=8 Source=0   (parameter_reaches_result_string.ash:1:21)",
        "    Return                Source=2   (parameter_reaches_result_string.ash:1:1)"
    ])

// A string parameter reaching the result only as a list cell's head is not normalized at entry:
// the arena cell keeps nothing of an owned copy alive, so the cell borrows the argument.
let testStringParameterInCellIsNotNormalized unit =
    Unit
    |> stringParameterSource
    |> loweredLines("parameter_reaches_result_string.ash")
    |> functionText("lambda_0")
    |> test.assertEqual(stringParameterFunction(Unit))

// The closure carrying that function accepts no runtime-managed argument.
let testStringParameterClosureTakesNoOwnedArgument unit =
    Unit
    |> stringParameterSource
    |> loweredLines("parameter_reaches_result_string.ash")
    |> anyLineContains("MakeClosureStack      Target=1 FuncLabel=lambda_0 EnvPtrTemp=0 EnvSizeBytes=0   (parameter_reaches_result_string.ash:1:1)")
    |> test.assertEqual(true)

let recordParameterSource unit = "type Pair =\n    | label: Str\n    | count: Int\n\nlet wrap (pair: Pair) = [pair]\n\nmatch wrap(Pair(label = Ashes.Text.fromInt(3), count = 1)) with\n    | Pair { label = label } :: [] -> Ashes.IO.print(label)\n    | _ -> Ashes.IO.print(\"other\")\n"

let recordParameterFunction unit =
    Ashes.Text.join("\n")([
        "function lambda_0  [SourceFunction from wrap]",
        "  locals=10 temps=47",
        "    LoadConstInt          Target=0 Value=0   (parameter_reaches_result_record.ash:5:25)",
        "    LoadLocal             Target=1 Slot=1   (parameter_reaches_result_record.ash:5:26)",
        "    Alloc                 Target=2 SizeBytes=16   (parameter_reaches_result_record.ash:5:25)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=0 Source=1   (parameter_reaches_result_record.ash:5:25)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=8 Source=0   (parameter_reaches_result_record.ash:5:25)",
        "    IsReferenceCounted    Target=3 SourceTemp=2",
        "    JumpIfFalse           CondTemp=3 Target=rc_representation_copy_0   (parameter_reaches_result_record.ash:5:1)",
        "    RcDup                 Target=4 SourceTemp=2 RuntimeManaged=true",
        "    StoreLocal            Slot=2 Source=4   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_representation_done_1   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_copy_0:",
        "    IsReferenceCounted    Target=6 SourceTemp=2",
        "    JumpIfFalse           CondTemp=6 Target=rc_representation_copy_2   (parameter_reaches_result_record.ash:5:1)",
        "    RcDup                 Target=7 SourceTemp=2 RuntimeManaged=true",
        "    StoreLocal            Slot=3 Source=7   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_representation_done_3   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_copy_2:",
        "    LoadConstInt          Target=8 Value=0   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=4 Source=2   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=5 Source=8   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=6 Source=8   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_4:",
        "    LoadLocal             Target=9 Slot=4   (parameter_reaches_result_record.ash:5:1)",
        "    CmpIntNe              Target=10 Left=9 Right=8   (parameter_reaches_result_record.ash:5:1)",
        "    JumpIfFalse           CondTemp=10 Target=rc_normalize_list_end_5   (parameter_reaches_result_record.ash:5:1)",
        "    IsReferenceCounted    Target=11 SourceTemp=9",
        "    JumpIfFalse           CondTemp=11 Target=rc_normalize_list_cell_6   (parameter_reaches_result_record.ash:5:1)",
        "    RcDup                 Target=12 SourceTemp=9 RuntimeManaged=true",
        "    LoadLocal             Target=13 Slot=5   (parameter_reaches_result_record.ash:5:1)",
        "    CmpIntNe              Target=14 Left=13 Right=8   (parameter_reaches_result_record.ash:5:1)",
        "    JumpIfFalse           CondTemp=14 Target=rc_normalize_list_first_7   (parameter_reaches_result_record.ash:5:1)",
        "    LoadLocal             Target=15 Slot=6   (parameter_reaches_result_record.ash:5:1)",
        "    StoreMemOffset        BasePtr=15 OffsetBytes=8 Source=12   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_normalize_list_linked_8   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_first_7:",
        "    StoreLocal            Slot=5 Source=12   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_linked_8:",
        "    StoreLocal            Slot=6 Source=12   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_normalize_list_end_5   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_cell_6:",
        "    LoadMemOffset         Target=16 BasePtr=9 OffsetBytes=0   (parameter_reaches_result_record.ash:5:1)",
        "    LoadMemOffset         Target=17 BasePtr=9 OffsetBytes=8   (parameter_reaches_result_record.ash:5:1)",
        "    IsReferenceCounted    Target=18 SourceTemp=16",
        "    JumpIfFalse           CondTemp=18 Target=rc_representation_copy_9   (parameter_reaches_result_record.ash:5:1)",
        "    RcDup                 Target=19 SourceTemp=16 RuntimeManaged=true",
        "    StoreLocal            Slot=7 Source=19   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_representation_done_10   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_copy_9:",
        "    LoadConstInt          Target=21 Value=0   (parameter_reaches_result_record.ash:5:1)",
        "    CallKnown             Target=22 FuncLabel=__rcnorm_1 EnvTemp=21 ArgTemp=16   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=7 Source=22   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_done_10:",
        "    LoadLocal             Target=23 Slot=7   (parameter_reaches_result_record.ash:5:1)",
        "    Alloc                 Target=24 SizeBytes=16 RuntimeManaged=true   (parameter_reaches_result_record.ash:5:1)",
        "    StoreMemOffset        BasePtr=24 OffsetBytes=0 Source=23   (parameter_reaches_result_record.ash:5:1)",
        "    StoreMemOffset        BasePtr=24 OffsetBytes=8 Source=8   (parameter_reaches_result_record.ash:5:1)",
        "    LoadLocal             Target=25 Slot=5   (parameter_reaches_result_record.ash:5:1)",
        "    CmpIntNe              Target=26 Left=25 Right=8   (parameter_reaches_result_record.ash:5:1)",
        "    JumpIfFalse           CondTemp=26 Target=rc_normalize_list_first_15   (parameter_reaches_result_record.ash:5:1)",
        "    LoadLocal             Target=27 Slot=6   (parameter_reaches_result_record.ash:5:1)",
        "    StoreMemOffset        BasePtr=27 OffsetBytes=8 Source=24   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_normalize_list_linked_16   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_first_15:",
        "    StoreLocal            Slot=5 Source=24   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_linked_16:",
        "    StoreLocal            Slot=6 Source=24   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=4 Source=17   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_normalize_list_4   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_end_5:",
        "    LoadLocal             Target=28 Slot=5   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=3 Source=28   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_done_3:",
        "    LoadLocal             Target=29 Slot=3   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=2 Source=29   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_done_1:",
        "    LoadLocal             Target=30 Slot=2   (parameter_reaches_result_record.ash:5:1)",
        "    LoadArgumentOwnership Target=31   (parameter_reaches_result_record.ash:5:1)",
        "    LoadConstInt          Target=32 Value=1   (parameter_reaches_result_record.ash:5:1)",
        "    ShrInt                Target=33 Left=31 Right=32   (parameter_reaches_result_record.ash:5:1)",
        "    AndInt                Target=34 Left=33 Right=32   (parameter_reaches_result_record.ash:5:1)",
        "    JumpIfFalse           CondTemp=34 Target=rc_result_owned_17   (parameter_reaches_result_record.ash:5:1)",
        "    Alloc                 Target=35 SizeBytes=8   (parameter_reaches_result_record.ash:5:1)",
        "    MakeClosure           Target=36 FuncLabel=__deepcopy_list_2 EnvPtrTemp=35 EnvSizeBytes=8   (parameter_reaches_result_record.ash:5:1)",
        "    StoreMemOffset        BasePtr=35 OffsetBytes=0 Source=36   (parameter_reaches_result_record.ash:5:1)",
        "    CallClosure           Target=37 ClosureTemp=36 ArgTemp=30   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=9 Source=30   (parameter_reaches_result_record.ash:5:1)",
        "  rcdrop_list_19:",
        "    LoadLocal             Target=38 Slot=9   (parameter_reaches_result_record.ash:5:1)",
        "    LoadConstInt          Target=39 Value=0   (parameter_reaches_result_record.ash:5:1)",
        "    CmpIntNe              Target=40 Left=38 Right=39   (parameter_reaches_result_record.ash:5:1)",
        "    JumpIfFalse           CondTemp=40 Target=rcdrop_list_end_21   (parameter_reaches_result_record.ash:5:1)",
        "    RcIsUnique            Target=41 SourceTemp=38",
        "    JumpIfFalse           CondTemp=41 Target=rcdrop_list_shared_20   (parameter_reaches_result_record.ash:5:1)",
        "    LoadMemOffset         Target=42 BasePtr=38 OffsetBytes=0   (parameter_reaches_result_record.ash:5:1)",
        "    LoadConstInt          Target=43 Value=0   (parameter_reaches_result_record.ash:5:1)",
        "    CallKnown             Target=44 FuncLabel=__rcdrop_record_4 EnvTemp=43 ArgTemp=42   (parameter_reaches_result_record.ash:5:1)",
        "    LoadMemOffset         Target=45 BasePtr=38 OffsetBytes=8   (parameter_reaches_result_record.ash:5:1)",
        "    RcDrop                SourceTemp=38 TypeName=List RuntimeManaged=true",
        "    StoreLocal            Slot=9 Source=45   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rcdrop_list_19   (parameter_reaches_result_record.ash:5:1)",
        "  rcdrop_list_shared_20:",
        "    RcDrop                SourceTemp=38 TypeName=List RuntimeManaged=true",
        "    Jump                  Target=rcdrop_list_end_21   (parameter_reaches_result_record.ash:5:1)",
        "  rcdrop_list_end_21:",
        "    StoreLocal            Slot=8 Source=37   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_result_done_18   (parameter_reaches_result_record.ash:5:1)",
        "  rc_result_owned_17:",
        "    StoreLocal            Slot=8 Source=30   (parameter_reaches_result_record.ash:5:1)",
        "  rc_result_done_18:",
        "    LoadLocal             Target=46 Slot=8   (parameter_reaches_result_record.ash:5:1)",
        "    Return                Source=46   (parameter_reaches_result_record.ash:5:1)"
    ])

// A record parameter with a string field reaching the result only as a list cell's head is not
// normalized at entry; the list the body builds around it is made the result's own, walked cell by
// cell with each element retained when reference-counted and normalized by its helper otherwise.
let testRecordParameterInCellResultNormalization unit =
    Unit
    |> recordParameterSource
    |> loweredLines("parameter_reaches_result_record.ash")
    |> functionText("lambda_0")
    |> test.assertEqual(recordParameterFunction(Unit))

let scalarParameterFunction unit =
    Ashes.Text.join("\n")([
        "function lambda_0  [SourceFunction from keep]",
        "  locals=4 temps=14",
        "    LoadConstInt          Target=0 Value=0   (scalar_parameter.ash:1:21)",
        "    LoadLocal             Target=1 Slot=1   (scalar_parameter.ash:1:22)",
        "    Alloc                 Target=2 SizeBytes=16 RuntimeManaged=true   (scalar_parameter.ash:1:21)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=0 Source=1   (scalar_parameter.ash:1:21)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=8 Source=0   (scalar_parameter.ash:1:21)",
        "    LoadArgumentOwnership Target=3   (scalar_parameter.ash:1:1)",
        "    LoadConstInt          Target=4 Value=1   (scalar_parameter.ash:1:1)",
        "    ShrInt                Target=5 Left=3 Right=4   (scalar_parameter.ash:1:1)",
        "    AndInt                Target=6 Left=5 Right=4   (scalar_parameter.ash:1:1)",
        "    JumpIfFalse           CondTemp=6 Target=rc_result_owned_0   (scalar_parameter.ash:1:1)",
        "    CopyOutList           DestTemp=7 SrcTemp=2 HeadCopy=Inline Purpose=ArenaResultBoundary   (scalar_parameter.ash:1:1)",
        "    StoreLocal            Slot=3 Source=2   (scalar_parameter.ash:1:1)",
        "  rcdrop_list_2:",
        "    LoadLocal             Target=8 Slot=3   (scalar_parameter.ash:1:1)",
        "    LoadConstInt          Target=9 Value=0   (scalar_parameter.ash:1:1)",
        "    CmpIntNe              Target=10 Left=8 Right=9   (scalar_parameter.ash:1:1)",
        "    JumpIfFalse           CondTemp=10 Target=rcdrop_list_end_4   (scalar_parameter.ash:1:1)",
        "    RcIsUnique            Target=11 SourceTemp=8",
        "    JumpIfFalse           CondTemp=11 Target=rcdrop_list_shared_3   (scalar_parameter.ash:1:1)",
        "    LoadMemOffset         Target=12 BasePtr=8 OffsetBytes=8   (scalar_parameter.ash:1:1)",
        "    RcDrop                SourceTemp=8 TypeName=List RuntimeManaged=true",
        "    StoreLocal            Slot=3 Source=12   (scalar_parameter.ash:1:1)",
        "    Jump                  Target=rcdrop_list_2   (scalar_parameter.ash:1:1)",
        "  rcdrop_list_shared_3:",
        "    RcDrop                SourceTemp=8 TypeName=List RuntimeManaged=true",
        "    Jump                  Target=rcdrop_list_end_4   (scalar_parameter.ash:1:1)",
        "  rcdrop_list_end_4:",
        "    StoreLocal            Slot=2 Source=7   (scalar_parameter.ash:1:1)",
        "    Jump                  Target=rc_result_done_1   (scalar_parameter.ash:1:1)",
        "  rc_result_owned_0:",
        "    StoreLocal            Slot=2 Source=2   (scalar_parameter.ash:1:1)",
        "  rc_result_done_1:",
        "    LoadLocal             Target=13 Slot=2   (scalar_parameter.ash:1:1)",
        "    Return                Source=13   (scalar_parameter.ash:1:1)"
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
    |> testCalleeNotReachingItsParameterDoesNotReach
    |> testStringParameterInCellIsNotNormalized
    |> testStringParameterClosureTakesNoOwnedArgument
    |> testRecordParameterInCellResultNormalization
    |> testScalarParameterIsNotNormalized
