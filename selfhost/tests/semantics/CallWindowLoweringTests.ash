import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.CoreLowering.Raw
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
import LoweredIrFixtures.loweredFixtureLines
import LoweredIrFixtures.stageZeroFixtureLines
import LoweredIrFixtures.functionLines
import LoweredIrFixtures.withoutLocations
import LoweredIrFixtures.withFunctionLocalLabels
import LoweredIrFixtures.expectSameLines
export (
    value runCallWindowLoweringTests,
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

// Whether some line, trimmed, begins with the instruction text (the source location that
// follows it is not part of the comparison).
let recursive hasInstruction (instruction: Str) (lines: List(Str)) =
    match lines with
        | [] -> false
        | line :: rest ->
            Ashes.Text.startsWith(Ashes.Text.trim(line))(instruction) || hasInstruction(instruction)(rest)

let recursive hasInstructionText (needle: Str) (lines: List(Str)) =
    match lines with
        | [] -> false
        | line :: rest -> Ashes.Text.contains(line)(needle) || hasInstructionText(needle)(rest)

// The lines before the entry function's header: the lifted functions.
let recursive liftedFunctionLines (lines: List(Str)) =
    match lines with
        | [] -> []
        | line :: rest ->
            if Ashes.Text.startsWith(line)("function _start_main")
            then []
            else line :: liftedFunctionLines(rest)

let expectInstruction (instruction: Str) (lines: List(Str)) =
    if hasInstruction(instruction)(lines)
    then lines
    else test.fail("expected instruction `" + instruction + "` in:\n" + Ashes.Text.join("\n")(lines))

let recursive expectOperands opcode operands lines =
    match lines with
        | [] -> test.fail("expected " + opcode + " with operands " + operands)
        | line :: rest ->
            if Ashes.Text.startsWith(Ashes.Text.trim(line))(opcode + " ") && Ashes.Text.contains(line)(operands)
            then rest
            else expectOperands(opcode)(operands)(rest)

let expectNoInstructionText (needle: Str) (lines: List(Str)) =
    if hasInstructionText(needle)(lines)
    then test.fail("expected no `" + needle + "` in:\n" + Ashes.Text.join("\n")(lines))
    else lines

// A helper returning `Unit` from an arena constructor: the caller cannot know the result's
// placement, so it reads the callee's returns bit and copies the result past the window's reset
// only when the bit is clear, reloading the slot either way.
let unitHelperProgram = "let shout (text: Str) = Ashes.IO.print(text)\n\nshout(Ashes.Text.fromInt(7))"

let expectUnknownResultPlacementReadsReturnsBit unit =
    unitHelperProgram
    |> dumpSource
    |> expectInstruction("LoadMemOffset         Target=6 BasePtr=3 OffsetBytes=16")
    |> expectInstruction("LoadConstInt          Target=7 Value=63")
    |> expectInstruction("ShrInt                Target=8 Left=6 Right=7")
    |> expectInstruction("CallClosure           Target=9 ClosureTemp=3 ArgTemp=5")
    |> expectInstruction("StoreLocal            Slot=6 Source=9")
    |> expectInstruction("RestoreArenaState     CursorLocalSlot=3 EndLocalSlot=4 PreRestoreEndSlot=5")
    |> expectInstruction("JumpIfFalse           CondTemp=8 Target=call_copy_arena_result_0")
    |> expectInstruction("Jump                  Target=call_reclaim_owned_result_1")
    |> expectInstruction("CopyOutArena          DestTemp=10 SrcTemp=9 StaticSizeBytes=8 RuntimeManaged=true Purpose=RcNormalization")
    |> expectInstruction("StoreLocal            Slot=6 Source=10")
    |> expectInstruction("ReclaimArenaChunks    SavedEndSlot=4 PreRestoreEndSlot=5")
    |> expectInstruction("LoadLocal             Target=11 Slot=6")
    |> (given (_) -> Unit)

// The reloaded call result is reference-counted: the enclosing top-level bracket resets its
// window and reloads instead of copying the result out again.
let expectNormalizedResultResetsEnclosingBracket unit =
    unitHelperProgram
    |> dumpSource
    |> expectInstruction("StoreLocal            Slot=7 Source=11")
    |> expectInstruction("ReclaimArenaChunks    SavedEndSlot=1 PreRestoreEndSlot=8")
    |> expectInstruction("LoadLocal             Target=13 Slot=7")
    |> expectNoInstructionText("DestTemp=12")
    |> (given (_) -> Unit)

// A fresh reference-counted argument to a helper whose result reach is unknown travels under the
// callee's accepts bit: the caller reads the bit and passes its own reference with the flag, without
// retaining a second one, and releases the argument after the call where the callee did not adopt it.
let freshArgumentProgram = "let makeText n =\n    let text = Ashes.Text.fromInt(n)\n    in text\n\nlet shout (text: Str) = Ashes.IO.print(text)\n\nshout(makeText(7))"

let expectFreshArgumentHandedOverUnderAcceptsBit unit =
    freshArgumentProgram
    |> dumpSource
    |> expectInstruction("LoadMemOffset         Target=10 BasePtr=5 OffsetBytes=16")
    |> expectInstruction("LoadConstInt          Target=11 Value=62")
    |> expectInstruction("ShrInt                Target=12 Left=10 Right=11")
    |> expectInstruction("LoadConstInt          Target=13 Value=1")
    |> expectInstruction("AndInt                Target=14 Left=12 Right=13")
    |> expectInstruction("CallClosure           Target=18 ClosureTemp=5 ArgTemp=9 RuntimeManagedArgumentFlagTemp=14")
    |> expectInstruction("JumpIfFalse           CondTemp=14 Target=rc_handed_over_not_adopted_4")
    |> expectInstruction("RcDrop                SourceTemp=9 TypeName=String RuntimeManaged=true")
    |> expectNoInstructionText("rc_call_argument_not_retained")
    |> expectNoInstructionText("RcDup")
    |> (given (_) -> Unit)

// A helper whose result keeps its argument takes a fresh argument outright: the accepts bit is
// still passed, but the argument is neither retained nor released by the caller. The helper
// returns its entry-normalized parameter, so its closure carries the returns bit as well and
// the caller adopts the reference-counted result without a copy, releasing it after its use.
let expectKeptFreshArgumentIsTransferred unit =
    "let makeText n =\n    let text = Ashes.Text.fromInt(n)\n    in text\n\nlet keep (text: Str) = text\n\nAshes.IO.print(keep(makeText(7)))"
    |> dumpSource
    |> expectInstruction("MakeClosureStack      Target=3 FuncLabel=lambda_1 EnvPtrTemp=2 EnvSizeBytes=0 ReturnsRuntimeManaged=true AcceptsRuntimeManagedArgument=true")
    |> expectInstruction("AndInt                Target=14 Left=12 Right=13")
    |> expectInstruction("CallClosure           Target=18 ClosureTemp=5 ArgTemp=9 RuntimeManagedArgumentFlagTemp=14")
    |> expectInstruction("PrintStr              Source=18")
    |> expectInstruction("RcDrop                SourceTemp=18 TypeName=String RuntimeManaged=true")
    |> expectNoInstructionText("DestTemp=19 SrcTemp=18")
    |> expectNoInstructionText("rc_call_argument_not_retained")
    |> expectNoInstructionText("RcDrop                SourceTemp=9")
    |> (given (_) -> Unit)

// A helper whose lowered body produced a reference-counted result: the result's ownership is
// statically known, so the window resets without reading the returns bit.
let expectKnownRuntimeManagedResultResetsWithoutFlag unit =
    "let shout n =\n    let text = Ashes.Text.fromInt(n)\n    in text\n\nAshes.IO.print(shout(7))"
    |> dumpSource
    |> expectInstruction("CallClosure           Target=5 ClosureTemp=3 ArgTemp=4")
    |> expectInstruction("RestoreArenaState     CursorLocalSlot=3 EndLocalSlot=4 PreRestoreEndSlot=5")
    |> expectNoInstructionText("Value=63")
    |> expectNoInstructionText("call_copy_arena_result")
    |> (given (_) -> Unit)

// A scalar result survives the reset: no flag, no copy-out.
let expectScalarResultKeepsPlainReset unit =
    "let double (n: Int) = n * 2\n\nAshes.IO.print(Ashes.Text.fromInt(double(4)))"
    |> dumpSource
    |> expectNoInstructionText("Value=63")
    |> expectNoInstructionText("call_copy_arena_result")
    |> (given (_) -> Unit)

// A self-recursive call keeps the plain scope rule: the backend fuses the call and its return
// into a native loop, which a copy-out block between them would break.
let expectSelfRecursiveCallKeepsWindowOpen unit =
    "let recursive repeat (n: Int) = if n == 0 then \"\" else repeat(n - 1)\n\nAshes.IO.print(repeat(3))"
    |> dumpSource
    |> liftedFunctionLines
    |> expectNoInstructionText("Value=63")
    |> expectNoInstructionText("call_copy_arena_result")
    |> (given (_) -> Unit)

// A curried recursive function's inner stage calls the member through a captured closure: it is
// still the self callee, and its window keeps the plain scope rule.
let expectCurriedSelfCallKeepsWindowOpen unit =
    "let recursive build (n: Int) (acc: List(Int)) = if n == 0 then acc else build(n - 1)(n :: acc)\n\nmatch build(3)([]) with\n    | [] -> Ashes.IO.print(\"empty\")\n    | _ -> Ashes.IO.print(\"full\")"
    |> dumpSource
    |> liftedFunctionLines
    |> expectNoInstructionText("Value=63")
    |> expectNoInstructionText("call_copy_arena_result")
    |> (given (_) -> Unit)

let constructorRecursiveProducerProgram = "let bang (n: Str) = n + \"!\"\n\nlet recursive stamp xs =\n    match xs with\n        | [] -> []\n        | head :: tail -> bang(head) :: stamp(tail)\n\nmatch stamp([\"a\"]) with\n    | [] -> Ashes.IO.print(\"empty\")\n    | _ -> Ashes.IO.print(\"full\")"

// A producer whose cons tail is its own saturated self call (`bang(head) :: stamp(tail)`) is
// built in a loop: each iteration allocates its cell on the reference-counted heap (the spine
// must be reference-counted, the transform's own eligibility gate), links it into the
// predecessor's tail, and takes the loop's back edge, so no self call and no call window remain
// inside the function. The chain closes at the single return, and the closed spine keeps the
// body's reference-counted representation: the closure carries `ReturnsRuntimeManaged=true`
// and the function ends in the result-ownership epilogue that copies the spine out for a caller
// that wants an arena result and releases the original. A list of strings is a result runtime
// RC holds, so the call site trusts the verified reference-counted result and restores its
// window without a copy-out, exactly as stage 0 lowers the same program; the only remaining
// difference is a dead zero store stage 0 emits before its epilogue.
let expectConstructorRecursiveProducerKeepsRuntimeManagedSpine unit =
    constructorRecursiveProducerProgram
    |> dumpSource
    |> expectInstruction("MakeClosure           Target=5 FuncLabel=lambda_1 EnvPtrTemp=2 EnvSizeBytes=8 ReturnsRuntimeManaged=true")
    |> expectInstruction("LoadConstInt          Target=11 Value=63")
    |> expectNoInstructionText("call_copy_arena_result")
    |> liftedFunctionLines
    |> expectNoInstructionText("Value=63")
    |> expectNoInstructionText("call_copy_arena_result")
    |> expectOperands("Alloc")(" SizeBytes=16 RuntimeManaged=true")
    |> expectOperands("JumpIfFalse")(" Target=tmc_first_")
    |> expectInstruction("Jump                  Target=lambda_1_body")
    |> expectOperands("JumpIfFalse")(" Target=tmc_close_empty_")
    |> expectOperands("AndInt")(" Left=")
    |> expectOperands("JumpIfFalse")(" Target=rc_result_owned_")
    |> expectOperands("CopyOutList")(" HeadCopy=String Purpose=ArenaResultBoundary")
    |> (given (_) -> Unit)

// The control for the rule above: this cons's tail is an ordinary parameter, not a call to a
// member of the enclosing recursive group, so nothing forces the spine onto the
// reference-counted heap and the cell keeps the plain arena placement.
let expectPlainConsTailStaysInTheArena unit =
    "let recursive prepend (value: Int) (rest: List(Int)) (extra: Int) =\n    if extra == 0\n    then value :: rest\n    else prepend(value)(rest)(extra - 1)\n\nmatch prepend(1)([2, 3])(0) with\n    | [] -> Ashes.IO.print(\"empty\")\n    | _ -> Ashes.IO.print(\"full\")"
    |> dumpSource
    |> liftedFunctionLines
    |> expectInstruction("Alloc                 Target=10 SizeBytes=16")
    |> expectNoInstructionText("Alloc                 Target=10 SizeBytes=16 RuntimeManaged")
    |> (given (_) -> Unit)

// A recursive group member's tail call to its sibling is the fused self call: no returns bit,
// no copy-out block between the call and the return.
let expectGroupSiblingTailCallKeepsWindowOpen unit =
    "let recursive ping (n: Int) = if n == 0 then \"\" else pong(n - 1)\nand pong (n: Int) = if n == 0 then \"!\" else ping(n - 1)\n\nAshes.IO.print(ping(3))"
    |> dumpSource
    |> liftedFunctionLines
    |> expectNoInstructionText("Value=63")
    |> expectNoInstructionText("call_copy_arena_result")
    |> (given (_) -> Unit)

// A saturated application of a curried let-bound function whose innermost stage produced a
// reference-counted result: the returned-closure chain is followed to that stage, so the window
// resets without reading the returns bit and the result is released after its use.
let expectCurriedKnownResultResetsWithoutFlag unit =
    "let describe (a: Int) (b: Int) = Ashes.Text.fromInt(a + b)\n\nAshes.IO.print(describe(3)(4))"
    |> dumpSource
    |> expectInstruction("CallClosure           Target=7 ClosureTemp=5 ArgTemp=6")
    |> expectInstruction("RestoreArenaState     CursorLocalSlot=3 EndLocalSlot=4 PreRestoreEndSlot=5")
    |> expectInstruction("RcDrop                SourceTemp=7 TypeName=String RuntimeManaged=true")
    |> expectNoInstructionText("Value=63")
    |> expectNoInstructionText("call_copy_arena_result")
    |> (given (_) -> Unit)

// A callee whose lowered body is reference-counted only through its scope's copy-out is not
// RC-eligible by provenance: the returns bit is still read before the call, as stage 0 reads
// it, while the recorded placement resets the window without the conditional copy-out.
let expectBodyRuntimeManagedNonEligibleCalleeReadsReturnsBit unit =
    "let report (n: Int) =\n    let text = Ashes.Text.fromInt(n) + \" items\"\n    in\n        let _ = Ashes.IO.print(text)\n        in Ashes.IO.print(text)\n\nreport(3)"
    |> dumpSource
    |> expectInstruction("LoadMemOffset         Target=5 BasePtr=3 OffsetBytes=16")
    |> expectInstruction("LoadConstInt          Target=6 Value=63")
    |> expectInstruction("ShrInt                Target=7 Left=5 Right=6")
    |> expectInstruction("CallClosure           Target=8 ClosureTemp=3 ArgTemp=4")
    |> expectInstruction("RestoreArenaState     CursorLocalSlot=3 EndLocalSlot=4 PreRestoreEndSlot=5")
    |> expectNoInstructionText("call_copy_arena_result")
    |> (given (_) -> Unit)

// A concatenation whose `let` consumer keeps the result reference-counted carries the runtime
// flag itself: its operands are lowered without the request, and the owner's release names the
// reference-counted value.
let expectConcatCarriesRuntimeFlag unit =
    "let report (n: Int) =\n    let text = Ashes.Text.fromInt(n) + \" items\"\n    in Ashes.IO.print(text)\n\nreport(3)"
    |> dumpSource
    |> expectInstruction("TextFromInt           Target=1 ValueTemp=0")
    |> expectNoInstructionText("TextFromInt           Target=1 ValueTemp=0 RuntimeManaged")
    |> expectInstruction("ConcatStr             Target=3 Left=1 Right=2 RuntimeManaged=true")
    |> expectInstruction("RcDrop                SourceTemp=3 TypeName=String OwnerSlot=4 RuntimeManaged=true")
    |> (given (_) -> Unit)

// A generic callee's list result over records (`mapAll(toItem)([1, 2, 3])`) has no call
// copy-out, so the program entry deep-copies it out of the call window through the
// `rc_normalize_list` walk, and the consumed argument is released outright after it, matching
// stage 0 instruction for instruction. `mapAll` binds its recursive call before consing onto
// it, so it is no tail-modulo-constructor producer and stage 0 does not route the call to an
// element specialization, which the self-hosted lowering does not have. Locations are left
// out: stage 0 tags the closure construction of a top-level `let` with its declaration span,
// which the self-hosted lowering does not attach yet, and the same gap keeps the whole program
// out of the parity runner. Labels are compared by their order within the entry.
let expectGenericListResultDeepCopyMatchesStageZero unit =
    "generic_list_result_deep_copy"
    |> loweredFixtureLines
    |> functionLines("[ProgramEntry]")
    |> withoutLocations
    |> withFunctionLocalLabels
    |> expectSameLines("generic list result deep copy program entry")("generic_list_result_deep_copy"
    |> stageZeroFixtureLines
    |> functionLines("[ProgramEntry]")
    |> withoutLocations
    |> withFunctionLocalLabels)

// A loop returning `Some` of a field of a record matched out of its list parameter keeps the
// pattern owner's duplicate of that field in an owned slot, which makes the return copy the arena
// `Some` and release the duplicate behind the copy, as stage 0 does. Locations and labels as above.
let expectLoopResultPartDuplicateMatchesStageZero unit =
    "loop_result_some_holding_a_field_of_a_list_head"
    |> loweredFixtureLines
    |> functionLines("[ClosureHelper from returnedClosureLabelOf]")
    |> withoutLocations
    |> withFunctionLocalLabels
    |> expectSameLines("loop result part duplicate")("loop_result_some_holding_a_field_of_a_list_head"
    |> stageZeroFixtureLines
    |> functionLines("[ClosureHelper from returnedClosureLabelOf]")
    |> withoutLocations
    |> withFunctionLocalLabels)

// A loop body whose deferred call-result copy-out renames a reload: the borrowed `expand(pattern)`
// call after it is rebuilt by the splice and still borrows, so placement takes no duplicate of the
// pattern owner for it, as stage 1 records the call by its target.
let expectBorrowedCallAfterRenamedReloadMatchesStageZero unit =
    "borrowed_call_after_renamed_reload"
    |> loweredFixtureLines
    |> functionLines("[ClosureHelper from expandCases]")
    |> withoutLocations
    |> withFunctionLocalLabels
    |> expectSameLines("borrowed call after a renamed reload")("borrowed_call_after_renamed_reload"
    |> stageZeroFixtureLines
    |> functionLines("[ClosureHelper from expandCases]")
    |> withoutLocations
    |> withFunctionLocalLabels)

// The final spine must have its own reference before releasing the list the producer consumed.
// The result-mode word describes that spine, and is written only once on this return path.
let recursive resultExitEvents (lines: List(Str)) =
    match lines with
        | [] -> []
        | line :: rest ->
            let events = resultExitEvents(rest)
            in
                if Ashes.Text.startsWith(Ashes.Text.trim(line))("tmc_close_done_")
                then "closed" :: events
                else
                    if Ashes.Text.startsWith(Ashes.Text.trim(line))("independent_result_not_requested_")
                    then "published" :: events
                    else
                        if Ashes.Text.startsWith(Ashes.Text.trim(line))("rc_tco_exit_drop_inactive_")
                        then "released" :: events
                        else events

let expectProducerPublishesClosedSpineBeforeExitRelease unit =
    match "aggregate_borrowing_owner_kept_by_callee"
    |> loweredFixtureLines
    |> functionLines("[SourceFunction from bump]")
    |> resultExitEvents with
        | "closed" :: "published" :: "released" :: [] -> Unit
        | events -> test.fail("expected spine closure, result publication and loop release in order, got " + Ashes.Text.join(", ")(events))

// Add(a, b) rebuilds Add(a + 1, b). Both fields are numbers, even when their bits happen
// to fall inside the reference-counted address range. The Name arm still stores a string.
let recursive scalarFieldInstructions (inside: Bool) (lines: List(Str)) =
    match lines with
        | [] -> test.fail("missing Add arm in bump")
        | line :: rest ->
            let instruction = Ashes.Text.trim(line)
            in
                if inside
                then
                    if Ashes.Text.startsWith(instruction)("AllocAdt")
                    then []
                    else instruction :: scalarFieldInstructions(true)(rest)
                else
                    scalarFieldInstructions(Ashes.Text.startsWith(instruction)("AddInt"))(rest)

let expectInlinePatternFieldNeverRetained unit =
    "record_head_list_producer"
    |> loweredFixtureLines
    |> functionLines("[SourceFunction from bump]")
    |> scalarFieldInstructions(false)
    |> expectNoInstructionText("IsReferenceCounted")
    |> expectNoInstructionText("RuntimeManaged=true")
    |> (given (_) -> Unit)

// Each of hop's two nonempty arms transfers its matched list tail. The second argument in the
// last arm is an unrelated integer whose spelling happens to match the first arm's tail.
let expectPatternTransfersUseTheirLexicalBinding unit =
    match Ashes.IO.File.readText("tests/tail_call_scalar_shadowing_another_arms_list_tail.ash") with
        | Error(message) -> test.fail(message)
        | Ok(source) ->
            match source
            |> dumpSource
            |> functionLines("[ClosureHelper from hop]")
            |> Ashes.Collection.List.filter(given (line) ->
                Ashes.Text.startsWith(Ashes.Text.trim(line))("RcDup") && Ashes.Text.contains(line)("RuntimeManaged=true MayBeEmpty=true")) with
                | _firstTail :: _secondTail :: [] -> Unit
                | _ -> test.fail("expected one nullable retain per transferred list tail")

// The words of an instruction line, its operands as `Name=value`.
let instructionWords (line: Str) =
    " "
    |> Ashes.Text.split(Ashes.Text.trim(line))
    |> Ashes.Collection.List.filter(given (word: Str) -> word != "")

// The value of a closure call's `Target=` operand.
let callTargetOf (line: Str) =
    match instructionWords(line) with
        | "CallClosure" :: target :: _rest ->
            match Ashes.Text.split(target)("=") with
                | "Target" :: value :: [] -> Some(value)
                | _ -> None
        | _ -> None

let recursive hasWord (wanted: Str) (words: List(Str)) =
    match words with
        | [] -> false
        | word :: rest -> word == wanted || hasWord(wanted)(rest)

// Whether the line retains `target`, and with `requireRuntimeManaged` whether it retains it as a
// reference-counted value rather than as the identity marker a pending loop parameter leaves.
let retainsCallTarget (requireRuntimeManaged: Bool) (target: Str) (line: Str) =
    match instructionWords(line) with
        | "RcDup" :: _retained :: source :: rest -> source == "SourceTemp=" + target && (!requireRuntimeManaged || hasWord("RuntimeManaged=true")(rest))
        | _ -> false

// How many closure calls have their result retained by the very next instruction.
let recursive retainedCallResultCount (requireRuntimeManaged: Bool) (lines: List(Str)) =
    match lines with
        | first :: second :: rest ->
            match callTargetOf(first) with
                | Some(target) ->
                    (if retainsCallTarget(requireRuntimeManaged)(target)(second)
                    then 1
                    else 0) + retainedCallResultCount(requireRuntimeManaged)(second :: rest)
                | None -> retainedCallResultCount(requireRuntimeManaged)(second :: rest)
        | _ -> 0

// A loop applying a function whose whole body reads a `Str` or `Bytes` field of its parameter to
// the loop's record parameter. Stage 0 admits the parameter to the reference-counted heap, forces
// both calls' result flags to one and retains each field right after the call instead of copying it
// out of the arena; the fixture must show both retains, or it was not read as stage 0 wrote it. The
// self-hosted lowering emits the same retain after both calls, as the marker of a pending loop
// parameter. It keeps this parameter in the arena (the loop-parameter admission gap the elaborated
// `tco_parameter_kept_by_borrowing_callee_result` fixture records), so finalize leaves the markers
// as identity copies and zeroes the forced flags, which brings the copy back; once the parameter is
// admitted the markers become stage 0's retains.
let expectFieldAccessorCallRetainsResult unit =
    (let expected =
        "field_accessor_call_retains_result"
        |> stageZeroFixtureLines
        |> withoutLocations
        |> retainedCallResultCount(true)
    in
        if expected != 2
        then test.fail("the stage-0 fixture field_accessor_call_retains_result shows " + Ashes.Text.fromInt(expected) + " retained accessor call results, not 2: it was not read as stage 0 wrote it")
        else
            let actual =
                "field_accessor_call_retains_result"
                |> loweredFixtureLines
                |> withoutLocations
                |> retainedCallResultCount(false)
            in
                if actual == expected
                then Unit
                else test.fail("expected a retain after each of the " + Ashes.Text.fromInt(expected) + " accessor calls as stage 0 has, found " + Ashes.Text.fromInt(actual)))

let runCallWindowLoweringTests unit =
    Unit
    |> (given (_) -> expectBorrowedCallAfterRenamedReloadMatchesStageZero(Unit))
    |> expectUnknownResultPlacementReadsReturnsBit
    |> (given (_) -> expectGenericListResultDeepCopyMatchesStageZero(Unit))
    |> (given (_) -> expectLoopResultPartDuplicateMatchesStageZero(Unit))
    |> (given (_) -> expectFieldAccessorCallRetainsResult(Unit))
    |> expectCurriedSelfCallKeepsWindowOpen
    |> expectNormalizedResultResetsEnclosingBracket
    |> expectFreshArgumentHandedOverUnderAcceptsBit
    |> expectKeptFreshArgumentIsTransferred
    |> expectKnownRuntimeManagedResultResetsWithoutFlag
    |> expectScalarResultKeepsPlainReset
    |> expectSelfRecursiveCallKeepsWindowOpen
    |> expectConstructorRecursiveProducerKeepsRuntimeManagedSpine
    |> expectProducerPublishesClosedSpineBeforeExitRelease
    |> expectInlinePatternFieldNeverRetained
    |> expectPatternTransfersUseTheirLexicalBinding
    |> expectPlainConsTailStaysInTheArena
    |> expectGroupSiblingTailCallKeepsWindowOpen
    |> expectCurriedKnownResultResetsWithoutFlag
    |> expectBodyRuntimeManagedNonEligibleCalleeReadsReturnsBit
    |> expectConcatCarriesRuntimeFlag
    |> (given (_) -> Ashes.IO.print("call window lowering tests passed"))
