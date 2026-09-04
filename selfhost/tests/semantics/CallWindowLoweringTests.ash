import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
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

// A fresh reference-counted argument to a helper that neither borrows nor keeps it: the caller
// reads the callee's accepts bit, retains the argument only when the bit is set, passes the slot's
// value with the flag, and releases the fresh argument after the call.
let freshArgumentProgram = "let makeText n =\n    let text = Ashes.Text.fromInt(n)\n    in text\n\nlet shout (text: Str) = Ashes.IO.print(text)\n\nshout(makeText(7))"

let expectFreshArgumentRetainedUnderAcceptsBit unit =
    freshArgumentProgram
    |> dumpSource
    |> expectInstruction("LoadMemOffset         Target=10 BasePtr=5 OffsetBytes=16")
    |> expectInstruction("LoadConstInt          Target=11 Value=62")
    |> expectInstruction("ShrInt                Target=12 Left=10 Right=11")
    |> expectInstruction("LoadConstInt          Target=13 Value=1")
    |> expectInstruction("AndInt                Target=14 Left=12 Right=13")
    |> expectInstruction("StoreLocal            Slot=11 Source=9")
    |> expectInstruction("JumpIfFalse           CondTemp=14 Target=rc_call_argument_not_retained_0")
    |> expectInstruction("RcDup                 Target=15 SourceTemp=9 RuntimeManaged=true")
    |> expectInstruction("StoreLocal            Slot=11 Source=15")
    |> expectInstruction("LoadLocal             Target=16 Slot=11")
    |> expectInstruction("CallClosure           Target=20 ClosureTemp=5 ArgTemp=16 RuntimeManagedArgumentFlagTemp=14")
    |> expectInstruction("RcDrop                SourceTemp=9 TypeName=String RuntimeManaged=true")
    |> (given (_) -> Unit)

// A helper whose result keeps its argument takes a fresh argument outright: the accepts bit is
// still passed, but the argument is neither retained nor released by the caller.
let expectKeptFreshArgumentIsTransferred unit =
    "let makeText n =\n    let text = Ashes.Text.fromInt(n)\n    in text\n\nlet keep (text: Str) = text\n\nAshes.IO.print(keep(makeText(7)))"
    |> dumpSource
    |> expectInstruction("AndInt                Target=14 Left=12 Right=13")
    |> expectInstruction("CallClosure           Target=18 ClosureTemp=5 ArgTemp=9 RuntimeManagedArgumentFlagTemp=14")
    |> expectInstruction("CopyOutArena          DestTemp=19 SrcTemp=18 RuntimeManaged=true Purpose=RcNormalization")
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

let runCallWindowLoweringTests unit =
    Unit
    |> expectUnknownResultPlacementReadsReturnsBit
    |> expectCurriedSelfCallKeepsWindowOpen
    |> expectNormalizedResultResetsEnclosingBracket
    |> expectFreshArgumentRetainedUnderAcceptsBit
    |> expectKeptFreshArgumentIsTransferred
    |> expectKnownRuntimeManagedResultResetsWithoutFlag
    |> expectScalarResultKeepsPlainReset
    |> expectSelfRecursiveCallKeepsWindowOpen
    |> (given (_) -> Ashes.IO.print("call window lowering tests passed"))
