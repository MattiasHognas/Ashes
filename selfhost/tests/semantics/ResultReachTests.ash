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
        "  locals=12 temps=58",
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
        "    LoadConstInt          Target=11 Value=0   (parameter_reaches_result_record.ash:5:1)",
        "    CallKnown             Target=12 FuncLabel=__rcnorm_1 EnvTemp=11 ArgTemp=3   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=3 Source=12   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_done_3:",
        "    LoadLocal             Target=13 Slot=3   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=2 Source=13   (parameter_reaches_result_record.ash:5:1)",
        "  rc_arg_normalize_done_1:",
        "    LoadLocal             Target=14 Slot=2   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=1 Source=14   (parameter_reaches_result_record.ash:5:1)",
        "    LoadConstInt          Target=0 Value=0   (parameter_reaches_result_record.ash:5:25)",
        "    LoadLocal             Target=1 Slot=1   (parameter_reaches_result_record.ash:5:26)",
        "    Alloc                 Target=2 SizeBytes=16   (parameter_reaches_result_record.ash:5:25)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=0 Source=1   (parameter_reaches_result_record.ash:5:25)",
        "    StoreMemOffset        BasePtr=2 OffsetBytes=8 Source=0   (parameter_reaches_result_record.ash:5:25)",
        "    IsReferenceCounted    Target=15 SourceTemp=2",
        "    JumpIfFalse           CondTemp=15 Target=rc_representation_copy_8   (parameter_reaches_result_record.ash:5:1)",
        "    RcDup                 Target=16 SourceTemp=2 RuntimeManaged=true",
        "    StoreLocal            Slot=4 Source=16   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_representation_done_9   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_copy_8:",
        "    IsReferenceCounted    Target=18 SourceTemp=2",
        "    JumpIfFalse           CondTemp=18 Target=rc_representation_copy_10   (parameter_reaches_result_record.ash:5:1)",
        "    RcDup                 Target=19 SourceTemp=2 RuntimeManaged=true",
        "    StoreLocal            Slot=5 Source=19   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_representation_done_11   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_copy_10:",
        "    LoadConstInt          Target=20 Value=0   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=6 Source=2   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=7 Source=20   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=8 Source=20   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_12:",
        "    LoadLocal             Target=21 Slot=6   (parameter_reaches_result_record.ash:5:1)",
        "    CmpIntNe              Target=22 Left=21 Right=20   (parameter_reaches_result_record.ash:5:1)",
        "    JumpIfFalse           CondTemp=22 Target=rc_normalize_list_end_13   (parameter_reaches_result_record.ash:5:1)",
        "    IsReferenceCounted    Target=23 SourceTemp=21",
        "    JumpIfFalse           CondTemp=23 Target=rc_normalize_list_cell_14   (parameter_reaches_result_record.ash:5:1)",
        "    RcDup                 Target=24 SourceTemp=21 RuntimeManaged=true",
        "    LoadLocal             Target=25 Slot=7   (parameter_reaches_result_record.ash:5:1)",
        "    CmpIntNe              Target=26 Left=25 Right=20   (parameter_reaches_result_record.ash:5:1)",
        "    JumpIfFalse           CondTemp=26 Target=rc_normalize_list_first_15   (parameter_reaches_result_record.ash:5:1)",
        "    LoadLocal             Target=27 Slot=8   (parameter_reaches_result_record.ash:5:1)",
        "    StoreMemOffset        BasePtr=27 OffsetBytes=8 Source=24   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_normalize_list_linked_16   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_first_15:",
        "    StoreLocal            Slot=7 Source=24   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_linked_16:",
        "    StoreLocal            Slot=8 Source=24   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_normalize_list_end_13   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_cell_14:",
        "    LoadMemOffset         Target=28 BasePtr=21 OffsetBytes=0   (parameter_reaches_result_record.ash:5:1)",
        "    LoadMemOffset         Target=29 BasePtr=21 OffsetBytes=8   (parameter_reaches_result_record.ash:5:1)",
        "    IsReferenceCounted    Target=30 SourceTemp=28",
        "    JumpIfFalse           CondTemp=30 Target=rc_representation_copy_17   (parameter_reaches_result_record.ash:5:1)",
        "    RcDup                 Target=31 SourceTemp=28 RuntimeManaged=true",
        "    StoreLocal            Slot=9 Source=31   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_representation_done_18   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_copy_17:",
        "    LoadConstInt          Target=33 Value=0   (parameter_reaches_result_record.ash:5:1)",
        "    CallKnown             Target=34 FuncLabel=__rcnorm_1 EnvTemp=33 ArgTemp=28   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=9 Source=34   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_done_18:",
        "    LoadLocal             Target=35 Slot=9   (parameter_reaches_result_record.ash:5:1)",
        "    Alloc                 Target=36 SizeBytes=16 RuntimeManaged=true   (parameter_reaches_result_record.ash:5:1)",
        "    StoreMemOffset        BasePtr=36 OffsetBytes=0 Source=35   (parameter_reaches_result_record.ash:5:1)",
        "    StoreMemOffset        BasePtr=36 OffsetBytes=8 Source=20   (parameter_reaches_result_record.ash:5:1)",
        "    LoadLocal             Target=37 Slot=7   (parameter_reaches_result_record.ash:5:1)",
        "    CmpIntNe              Target=38 Left=37 Right=20   (parameter_reaches_result_record.ash:5:1)",
        "    JumpIfFalse           CondTemp=38 Target=rc_normalize_list_first_19   (parameter_reaches_result_record.ash:5:1)",
        "    LoadLocal             Target=39 Slot=8   (parameter_reaches_result_record.ash:5:1)",
        "    StoreMemOffset        BasePtr=39 OffsetBytes=8 Source=36   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_normalize_list_linked_20   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_first_19:",
        "    StoreLocal            Slot=7 Source=36   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_linked_20:",
        "    StoreLocal            Slot=8 Source=36   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=6 Source=29   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_normalize_list_12   (parameter_reaches_result_record.ash:5:1)",
        "  rc_normalize_list_end_13:",
        "    LoadLocal             Target=40 Slot=7   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=5 Source=40   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_done_11:",
        "    LoadLocal             Target=41 Slot=5   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=4 Source=41   (parameter_reaches_result_record.ash:5:1)",
        "  rc_representation_done_9:",
        "    LoadLocal             Target=42 Slot=4   (parameter_reaches_result_record.ash:5:1)",
        "    LoadArgumentOwnership Target=43   (parameter_reaches_result_record.ash:5:1)",
        "    LoadConstInt          Target=44 Value=1   (parameter_reaches_result_record.ash:5:1)",
        "    ShrInt                Target=45 Left=43 Right=44   (parameter_reaches_result_record.ash:5:1)",
        "    JumpIfFalse           CondTemp=45 Target=rc_result_owned_21   (parameter_reaches_result_record.ash:5:1)",
        "    Alloc                 Target=46 SizeBytes=8   (parameter_reaches_result_record.ash:5:1)",
        "    MakeClosure           Target=47 FuncLabel=__deepcopy_list_2 EnvPtrTemp=46 EnvSizeBytes=8   (parameter_reaches_result_record.ash:5:1)",
        "    StoreMemOffset        BasePtr=46 OffsetBytes=0 Source=47   (parameter_reaches_result_record.ash:5:1)",
        "    CallClosure           Target=48 ClosureTemp=47 ArgTemp=42   (parameter_reaches_result_record.ash:5:1)",
        "    StoreLocal            Slot=11 Source=42   (parameter_reaches_result_record.ash:5:1)",
        "  rcdrop_list_23:",
        "    LoadLocal             Target=49 Slot=11   (parameter_reaches_result_record.ash:5:1)",
        "    LoadConstInt          Target=50 Value=0   (parameter_reaches_result_record.ash:5:1)",
        "    CmpIntNe              Target=51 Left=49 Right=50   (parameter_reaches_result_record.ash:5:1)",
        "    JumpIfFalse           CondTemp=51 Target=rcdrop_list_end_25   (parameter_reaches_result_record.ash:5:1)",
        "    RcIsUnique            Target=52 SourceTemp=49",
        "    JumpIfFalse           CondTemp=52 Target=rcdrop_list_shared_24   (parameter_reaches_result_record.ash:5:1)",
        "    LoadMemOffset         Target=53 BasePtr=49 OffsetBytes=0   (parameter_reaches_result_record.ash:5:1)",
        "    LoadConstInt          Target=54 Value=0   (parameter_reaches_result_record.ash:5:1)",
        "    CallKnown             Target=55 FuncLabel=__rcdrop_record_4 EnvTemp=54 ArgTemp=53   (parameter_reaches_result_record.ash:5:1)",
        "    LoadMemOffset         Target=56 BasePtr=49 OffsetBytes=8   (parameter_reaches_result_record.ash:5:1)",
        "    RcDrop                SourceTemp=49 TypeName=List RuntimeManaged=true",
        "    StoreLocal            Slot=11 Source=56   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rcdrop_list_23   (parameter_reaches_result_record.ash:5:1)",
        "  rcdrop_list_shared_24:",
        "    RcDrop                SourceTemp=49 TypeName=List RuntimeManaged=true",
        "    Jump                  Target=rcdrop_list_end_25   (parameter_reaches_result_record.ash:5:1)",
        "  rcdrop_list_end_25:",
        "    StoreLocal            Slot=10 Source=48   (parameter_reaches_result_record.ash:5:1)",
        "    Jump                  Target=rc_result_done_22   (parameter_reaches_result_record.ash:5:1)",
        "  rc_result_owned_21:",
        "    StoreLocal            Slot=10 Source=42   (parameter_reaches_result_record.ash:5:1)",
        "  rc_result_done_22:",
        "    LoadLocal             Target=57 Slot=10   (parameter_reaches_result_record.ash:5:1)",
        "    Return                Source=57   (parameter_reaches_result_record.ash:5:1)"
    ])

// A record parameter with a string field is a contract record: on the borrowed path its
// normalization helper copies it, and the list the body builds around it is made the result's
// own, walked cell by cell with each element normalized the same way.
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
    |> testCalleeNotReachingItsParameterDoesNotReach
    |> testStringParameterEntryNormalization
    |> testStringParameterClosureFlag
    |> testRecordParameterEntryNormalization
    |> testScalarParameterIsNotNormalized
