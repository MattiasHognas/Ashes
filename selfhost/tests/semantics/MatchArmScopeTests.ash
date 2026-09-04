import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreCapabilityLowering
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
export (
    value runMatchArmScopeTests,
)

let parsedProgram source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let loweredProgramSource source =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { program = Some(program), error = None } -> program
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("program lowering failed: " + text))
        | _ -> test.fail("program lowering produced no program")

let dumpSource source =
    formatIr(loweredProgramSource(source))(LoweredIr)(None)

let recursive containsLine (needle: Str) (lines: List(Str)) =
    match lines with
        | [] -> false
        | line :: rest ->
            if line == needle
            then true
            else containsLine(needle)(rest)

let recursive countLinesStartingWith (prefix: Str) (lines: List(Str)) =
    match lines with
        | [] -> 0
        | line :: rest ->
            if Ashes.Text.startsWith(line)(prefix)
            then 1 + countLinesStartingWith(prefix)(rest)
            else countLinesStartingWith(prefix)(rest)

let expectLine (needle: Str) (lines: List(Str)) =
    lines
    |> containsLine(needle)
    |> test.assertEqual(true)
    |> (given (_) -> lines)

let expectNoLine (needle: Str) (lines: List(Str)) =
    lines
    |> containsLine(needle)
    |> test.assertEqual(false)
    |> (given (_) -> lines)

let shapeTypes unit = "type Pair =\n    | left: Int\n    | right: Int\n\ntype Shape =\n    | Dot\n    | Box(Pair)\n\n"

let swappedRecordSource unit = shapeTypes(Unit) + "let swapped =\n    match Box(Pair(left = 1, right = 2)) with\n        | Box(pair) -> Pair(left = pair.right, right = pair.left)\n        | Dot -> Pair(left = 0, right = 0)\n\n0"

// A tag-group dispatch brackets each linearly tested case of a group with its own cleanup block
// (`match_group_next` allocated before `match_arm_cleanup`), and a trivial single-case group on
// its success path only: the switch already proved the tag, so it gets no cleanup block.
let expectTagGroupCasesAreBracketed unit =
    "type Token =\n    | Word(Str)\n    | Number(Int)\n    | Space\n\nlet weight token =\n    match token with\n        | Number(0) -> 0\n        | Number(n) -> n\n        | Space -> 1\n        | Word(_) -> 2\n\n0"
    |> dumpSource
    |> expectLine("    SwitchTag             TagTemp=1 Cases=[3] DefaultLabel=match_none_1")
    |> expectLine("    SaveArenaState        CursorLocalSlot=3 EndLocalSlot=4")
    |> expectLine("    JumpIfFalse           CondTemp=9 Target=match_arm_cleanup_6")
    |> expectLine("    RestoreArenaState     CursorLocalSlot=3 EndLocalSlot=4 PreRestoreEndSlot=5")
    |> expectLine("  match_arm_cleanup_6:")
    |> expectLine("    RestoreArenaState     CursorLocalSlot=3 EndLocalSlot=4 PreRestoreEndSlot=6")
    |> expectLine("    Jump                  Target=match_group_next_5")
    |> expectLine("  match_group_next_5:")
    |> expectLine("    SaveArenaState        CursorLocalSlot=7 EndLocalSlot=8")
    |> expectLine("  match_arm_cleanup_7:")
    |> expectLine("    RestoreArenaState     CursorLocalSlot=7 EndLocalSlot=8 PreRestoreEndSlot=11")
    |> expectLine("    Jump                  Target=match_none_1")
    |> expectLine("    SaveArenaState        CursorLocalSlot=12 EndLocalSlot=13")
    |> expectLine("    RestoreArenaState     CursorLocalSlot=12 EndLocalSlot=13 PreRestoreEndSlot=14")
    |> expectLine("    SaveArenaState        CursorLocalSlot=15 EndLocalSlot=16")
    |> expectLine("    RestoreArenaState     CursorLocalSlot=15 EndLocalSlot=16 PreRestoreEndSlot=17")
    |> countLinesStartingWith("  match_arm_cleanup_")
    |> test.assertEqual(2)

// A record arm result of an arm whose pattern owns a heap binding is copied past the arm's reset:
// the owner's release anchors at the arm exit, the tagless two-field cell copies its 16 bytes as
// an RC-normalized value, and the copy replaces the result in the match's slot.
let expectRecordArmResultIsCopiedOutPastTheReset unit =
    Unit
    |> swappedRecordSource
    |> dumpSource
    |> expectLine("    RcDrop                SourceTemp=9 TypeName=Pair OwnerSlot=5")
    |> expectLine("    StoreLocal            Slot=2 Source=14")
    |> expectLine("    RestoreArenaState     CursorLocalSlot=3 EndLocalSlot=4 PreRestoreEndSlot=6")
    |> expectLine("    CopyOutArena          DestTemp=16 SrcTemp=14 StaticSizeBytes=16 RuntimeManaged=true Purpose=RcNormalization")
    |> expectLine("    ReclaimArenaChunks    SavedEndSlot=4 PreRestoreEndSlot=6")
    |> expectLine("    StoreLocal            Slot=2 Source=16")
    |> (given (_) -> Unit)

// An arm that owns nothing leaves its window open around a heap result: the pre-restore slot is
// allocated, but only the cleanup block restores the arm's bracket.
let expectOwnerlessHeapArmLeavesWindowOpen unit =
    Unit
    |> swappedRecordSource
    |> dumpSource
    |> expectLine("    StoreLocal            Slot=2 Source=24")
    |> expectNoLine("    RestoreArenaState     CursorLocalSlot=8 EndLocalSlot=9 PreRestoreEndSlot=10")
    |> expectLine("    RestoreArenaState     CursorLocalSlot=8 EndLocalSlot=9 PreRestoreEndSlot=11")
    |> (given (_) -> Unit)

// A record field receiver is loaded straight from its slot, without the owned-read borrow.
let expectRecordFieldReceiverIsReadWithoutBorrow unit =
    Unit
    |> swappedRecordSource
    |> dumpSource
    |> expectLine("    LoadLocal             Target=10 Slot=5")
    |> expectLine("    GetAdtField           Target=11 Ptr=10 FieldIndex=1 Tagless=true")
    |> expectNoLine("    Borrow                Target=11 SourceTemp=10")
    |> (given (_) -> Unit)

// A list over scalars copies its spine past the reset.
let expectListArmResultIsCopiedOutPastTheReset unit =
    shapeTypes(Unit) + "let sizes =\n    match Box(Pair(left = 3, right = 4)) with\n        | Box(pair) -> [pair.left, pair.right]\n        | Dot -> []\n\n0"
    |> dumpSource
    |> expectLine("    RcDrop                SourceTemp=9 TypeName=Pair OwnerSlot=5")
    |> expectLine("    CopyOutList           DestTemp=18 SrcTemp=16 HeadCopy=Inline RuntimeManaged=true Purpose=RcNormalization")
    |> expectLine("    StoreLocal            Slot=2 Source=18")
    |> (given (_) -> Unit)

// A tagged same-arity ADT copies its tag word along with its one field.
let expectTaggedSameArityArmResultCopiesItsTagWord unit =
    shapeTypes(Unit) + "type Two =\n    | A(Int)\n    | B(Int)\n\nlet chosen =\n    match Box(Pair(left = 1, right = 2)) with\n        | Box(pair) -> A(pair.left)\n        | Dot -> B(0)\n\n0"
    |> dumpSource
    |> expectLine("    CopyOutArena          DestTemp=14 SrcTemp=12 StaticSizeBytes=16 RuntimeManaged=true Purpose=RcNormalization")
    |> expectLine("    StoreLocal            Slot=2 Source=14")
    |> (given (_) -> Unit)

// A pattern-owned binding inside a lambda's match arm is released at the arm exit as well.
let expectLambdaArmReleasesItsPatternOwner unit =
    shapeTypes(Unit) + "let describe shape =\n    match shape with\n        | Box(pair) -> Pair(left = pair.right, right = pair.left)\n        | Dot -> Pair(left = 0, right = 0)\n\n0"
    |> dumpSource
    |> expectLine("function lambda_0  [SourceFunction from describe]")
    |> expectLine("    RcDrop                SourceTemp=6 TypeName=Pair OwnerSlot=5")
    |> expectLine("    GetAdtField           Target=8 Ptr=7 FieldIndex=1 Tagless=true")
    |> (given (_) -> Unit)

let resumeCall value = ExprCall(ExprVar("resume"))(ExprInt(value))(false)(callArgumentsInline)

// A capability-operation arm whose body matches on its parameter brackets each arm of that
// match like a linear arm: a save before the pattern test, and a cleanup block per arm.
let expectOperationArmsAreBracketed unit =
    [
        (Some("State"), "get", [PatternVar("u")], ExprMatch(
            ExprVar("u"),
            [(PatternInt(0), resumeCall(1), None), (PatternWildcard, resumeCall(2), None)],
            None
        ))
    ]
    |> ExprHandle(
        ExprInt(42)
    )
    |> lowerCoreExpressionWithCompleteContext([])([])([])([])([])([
        CoreCapabilityLayout(name = "State", index = 0, operations = [CoreCapabilityOperationLayout(name = "get", index = 0)])
    ])([])(1)
    |> (given (result) ->
        match result with
            | CoreLoweringResult { program = Some(program), error = None } -> formatIr(program)(LoweredIr)(None)
            | CoreLoweringResult { error = Some(error) } -> test.fail("handle lowering failed: " + Ashes.Trait.Show.show(error))
            | _ -> test.fail("handle lowering produced no program"))
    |> (given (lines) ->
        lines
        |> countLinesStartingWith("  match_arm_cleanup_")
        |> test.assertEqual(2)
        |> (given (_) -> lines))
    |> countLinesStartingWith("    SaveArenaState")
    |> (given (saves) -> test.assertEqual(true)(saves >= 2))

let runMatchArmScopeTests unit =
    Unit
    |> expectTagGroupCasesAreBracketed
    |> expectRecordArmResultIsCopiedOutPastTheReset
    |> expectOwnerlessHeapArmLeavesWindowOpen
    |> expectRecordFieldReceiverIsReadWithoutBorrow
    |> expectListArmResultIsCopiedOutPastTheReset
    |> expectTaggedSameArityArmResultCopiesItsTagWord
    |> expectLambdaArmReleasesItsPatternOwner
    |> expectOperationArmsAreBracketed
    |> (given (_) -> Ashes.IO.print("all self-hosted match arm scope tests passed"))
