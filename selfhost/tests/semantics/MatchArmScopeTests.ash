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

let recursive countLinesContaining (needle: Str) (lines: List(Str)) =
    match lines with
        | [] -> 0
        | line :: rest ->
            if Ashes.Text.contains(line)(needle)
            then 1 + countLinesContaining(needle)(rest)
            else countLinesContaining(needle)(rest)

let shoutSource unit = "let shout n = Ashes.Text.fromInt(n)\n\n"

// A fresh reference-counted string matched directly is owned by whichever arm matches it: each
// arm stores it into an owner slot of its own after its pattern test and releases it at the
// arm exit (the placement pass moves the release to the store), and the literal arm's result
// is copied past the arm's reset since the arm owned a live value.
let expectFreshStringScrutineeIsOwnedByEachArm unit =
    shoutSource(Unit) + "let described =\n    match shout(7) with\n        | \"7\" -> \"seven\"\n        | _ -> shout(8)\n\n0"
    |> dumpSource
    |> expectLine("    StoreLocal            Slot=11 Source=5")
    |> expectLine("    RcDrop                SourceTemp=5 TypeName=String OwnerSlot=11 RuntimeManaged=true")
    |> expectLine("    CopyOutArena          DestTemp=10 SrcTemp=8 RuntimeManaged=true Purpose=RcNormalization")
    |> expectLine("    StoreLocal            Slot=16 Source=5")
    |> expectLine("    RcDrop                SourceTemp=5 TypeName=String OwnerSlot=16 RuntimeManaged=true")
    |> countLinesContaining("OwnerSlot=22 RuntimeManaged=true")
    |> test.assertEqual(1)

// A match whose every arm stores a freshly produced reference-counted value is itself newly
// produced: the closure of a function with such a body returns runtime-managed.
let expectMatchJoinOfFreshArmsIsRuntimeManaged unit =
    shoutSource(Unit) + "let describe n =\n    match shout(n) with\n        | \"7\" -> \"seven\"\n        | _ -> shout(8)\n\n0"
    |> dumpSource
    |> expectLine("    MakeClosureStack      Target=5 FuncLabel=lambda_1 EnvPtrTemp=2 EnvSizeBytes=8 ReturnsRuntimeManaged=true")
    |> (given (_) -> Unit)

// Beside a fresh-string arm, a literal string arm is normalized to the reference-counted heap:
// the constant is loaded at the match's location and copied as an RC-normalized value, so the
// arm resets its window; the fresh-string arm itself keeps its arena placement, so the join is
// not uniformly runtime-managed and the closure carries no returns bit.
let expectLiteralStringArmIsNormalizedBesideAFreshStringArm unit =
    "let label n =\n    match Ashes.Text.fromInt(n) with\n        | \"7\" -> \"seven\"\n        | _ -> Ashes.Text.fromInt(8)\n\n0"
    |> dumpSource
    |> expectLine("    LoadConstStr          Target=4 StrLabel=str_1")
    |> expectLine("    CopyOutArena          DestTemp=5 SrcTemp=4 RuntimeManaged=true Purpose=RcNormalization")
    |> expectLine("    StoreLocal            Slot=2 Source=5")
    |> expectLine("    RestoreArenaState     CursorLocalSlot=3 EndLocalSlot=4 PreRestoreEndSlot=5")
    |> expectLine("    TextFromInt           Target=7 ValueTemp=6")
    |> expectLine("    MakeClosureStack      Target=1 FuncLabel=lambda_0 EnvPtrTemp=0 EnvSizeBytes=0")
    |> (given (_) -> Unit)

// A nested match whose arms are a copied-out list and the empty list joins as a runtime-managed
// list; matched directly, each outer arm owns it and walks its spine inline at the arm exit.
let expectRuntimeManagedListScrutineeWalksItsSpineAtTheArmExit unit =
    shapeTypes(Unit) + "let shape = Box(Pair(left = 3, right = 4))\n\nlet leading =\n    match (match shape with\n        | Box(pair) -> [pair.left, pair.right]\n        | Dot -> []) with\n        | head :: _ -> head\n        | [] -> 0\n\n0"
    |> dumpSource
    |> expectLine("    StoreLocal            Slot=19 Source=28")
    |> expectLine("  rcdrop_list_9:")
    |> expectLine("    RcIsUnique            Target=38 SourceTemp=35")
    |> expectLine("    RcDrop                SourceTemp=35 TypeName=List RuntimeManaged=true")
    |> expectLine("    StoreLocal            Slot=25 Source=28")
    |> expectLine("  rcdrop_list_13:")
    |> countLinesStartingWith("  rcdrop_list")
    |> test.assertEqual(6)

let recursive countLinesContainingBoth (first: Str) (second: Str) (lines: List(Str)) =
    match lines with
        | [] -> 0
        | line :: rest ->
            if Ashes.Text.contains(line)(first) && Ashes.Text.contains(line)(second)
            then 1 + countLinesContainingBoth(first)(second)(rest)
            else countLinesContainingBoth(first)(second)(rest)

// An arm whose pattern binds the whole scrutinee, or a heap value out of it, does not take the
// owner: the binding may leave the arm as its result, so only the binding's own release remains,
// and the scrutinee is not stored to a runtime-managed owner slot.
let expectWholeBindingArmTakesNoScrutineeOwner unit =
    shoutSource(Unit) + "let described =\n    match shout(7) with\n        | other -> other\n\n0"
    |> dumpSource
    |> expectLine("    StoreLocal            Slot=11 Source=5")
    |> expectLine("    RcDrop                SourceTemp=5 TypeName=String OwnerSlot=11")
    |> countLinesContainingBoth("OwnerSlot=11")("RuntimeManaged=true")
    |> test.assertEqual(0)

let returnArmMatch unit =
    ExprMatch(
        ExprVar("r"),
        [(PatternInt(42), ExprInt(1), None), (PatternWildcard, ExprInt(0), None)],
        None
    )

let handledLines unit =
    [
        (Some("State"), "get", [PatternVar("u")], resumeCall(1)),
        (None, "return", [PatternVar("r")], returnArmMatch(Unit))
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

// With a capability in the program, every arm reset of a match inside a handler is guarded by
// the live-posts counter: the counter (one past the pending-post register) is compared with
// zero and the restore/reclaim is skipped while a one-shot post is pending, on the arm's
// success path and in its cleanup block alike.
let expectArmResetsAreGuardedByLivePostsUnderAHandle unit =
    Unit
    |> handledLines
    |> (given (lines) ->
        lines
        |> countLinesStartingWith("  live_posts_skip_")
        |> (given (guards) -> test.assertEqual(true)(guards >= 4))
        |> (given (_) -> lines))
    |> (given (lines) ->
        lines
        |> countLinesContaining("LoadCapabilityHandler Target=")
        |> (given (loads) -> test.assertEqual(true)(loads >= 4))
        |> (given (_) -> lines))
    |> countLinesContaining("CapabilityIndex=2")
    |> (given (counters) -> test.assertEqual(true)(counters >= 4))

// Without a capability the arms reset unguarded.
let expectArmResetsAreUnguardedWithoutACapability unit =
    Unit
    |> swappedRecordSource
    |> dumpSource
    |> countLinesStartingWith("  live_posts_skip_")
    |> test.assertEqual(0)

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
    |> expectFreshStringScrutineeIsOwnedByEachArm
    |> expectMatchJoinOfFreshArmsIsRuntimeManaged
    |> expectLiteralStringArmIsNormalizedBesideAFreshStringArm
    |> expectRuntimeManagedListScrutineeWalksItsSpineAtTheArmExit
    |> expectWholeBindingArmTakesNoScrutineeOwner
    |> expectArmResetsAreGuardedByLivePostsUnderAHandle
    |> expectArmResetsAreUnguardedWithoutACapability
    |> (given (_) -> Ashes.IO.print("all self-hosted match arm scope tests passed"))
