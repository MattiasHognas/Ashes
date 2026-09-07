import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
export (
    value runCapabilityProgramLoweringTests,
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

let loweringErrorFor source =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { error = Some(error) } -> error
        | CoreLoweringResult { error = None } -> test.fail("expected program lowering to fail, but it produced a program")

let recursive anyLineContains (fragment: Str) (lines: List(Str)) =
    match lines with
        | [] -> false
        | line :: rest -> Ashes.Text.contains(line)(fragment) || anyLineContains(fragment)(rest)

let expectContains (fragment: Str) (lines: List(Str)) =
    lines
    |> anyLineContains(fragment)
    |> test.assertEqual(true)

let handlerGlobalsOf (program: IrProgram) =
    match program with
        | IrProgram { capabilityHandlerGlobals = count } -> count

let tagSource = "capability Tag =\n    | tag : Str -> Str\n\nlet decorate (s: Str) =\n    handle Tag.tag(s) with\n        | Tag.tag(text) -> resume(text)\n        | return(r) -> r\n\n"

// A program's own capability declaration registers the handler layout the handle installs and
// the implicit perform site dispatches through, and reserves the evidence globals the backend
// defines: one per capability plus the post register and the live-post counter.
let expectProgramCapabilityLowersHandlerAndPerform unit =
    tagSource + "decorate(\"abc\")"
    |> loweredProgramSource
    |> (given (program) ->
        Unit
        |> (given (_) ->
            program
            |> handlerGlobalsOf
            |> test.assertEqual(3))
        |> (given (_) ->
            None
            |> formatIr(program)(LoweredIr)
            |> (given (lines) ->
                Unit
                |> (given (_) -> expectContains("StoreCapabilityHandler")(lines))
                |> (given (_) -> expectContains("LoadCapabilityHandler")(lines))
                |> (given (_) -> expectContains("CapabilityIndex=2")(lines))
                |> (given (_) -> expectContains("Target=capability_unhandled_")(lines))
                |> (given (_) -> expectContains("capability_posts_loop_")(lines)))))

// Capabilities are numbered in declaration order, whichever declaration is used first.
let expectSecondCapabilityTakesTheNextIndex unit =
    "capability First =\n    | one : Int -> Int\n\ncapability Second =\n    | two : Int -> Int\n\nlet run (n: Int) =\n    handle Second.two(n) with\n        | Second.two(m) -> resume(m + 1)\n        | return(r) -> r\n\nrun(1)"
    |> loweredProgramSource
    |> (given (program) ->
        Unit
        |> (given (_) ->
            program
            |> handlerGlobalsOf
            |> test.assertEqual(4))
        |> (given (_) ->
            None
            |> formatIr(program)(LoweredIr)
            |> expectContains("CapabilityIndex=3")))

// A program without capabilities reserves no evidence globals.
let expectCapabilityFreeProgramReservesNoGlobals unit =
    "let a = 1\na + 1"
    |> loweredProgramSource
    |> handlerGlobalsOf
    |> test.assertEqual(0)

let expectReservedCapabilityNameIsRejected unit =
    "capability FileRead =\n    | read : Str -> Str\n\n1"
    |> loweringErrorFor
    |> (given (error) ->
        match error with
            | ReservedCapabilityName("FileRead") -> Unit
            | other -> test.fail("expected ReservedCapabilityName, got " + Ashes.Trait.Show.show(other)))

let expectDuplicateCapabilityNameIsRejected unit =
    "capability Tag =\n    | tag : Str -> Str\n\ncapability Tag =\n    | other : Str -> Str\n\n1"
    |> loweringErrorFor
    |> (given (error) ->
        match error with
            | DuplicateCapabilityName("Tag") -> Unit
            | other -> test.fail("expected DuplicateCapabilityName, got " + Ashes.Trait.Show.show(other)))

let expectDuplicateOperationIsRejected unit =
    "capability Tag =\n    | tag : Str -> Str\n    | tag : Int -> Int\n\n1"
    |> loweringErrorFor
    |> (given (error) ->
        match error with
            | DuplicateCapabilityOperationName("Tag", "tag") -> Unit
            | other -> test.fail("expected DuplicateCapabilityOperationName, got " + Ashes.Trait.Show.show(other)))

let runCapabilityProgramLoweringTests unit =
    unit
    |> expectProgramCapabilityLowersHandlerAndPerform
    |> expectSecondCapabilityTakesTheNextIndex
    |> expectCapabilityFreeProgramReservesNoGlobals
    |> expectReservedCapabilityNameIsRejected
    |> expectDuplicateCapabilityNameIsRejected
    |> expectDuplicateOperationIsRejected
