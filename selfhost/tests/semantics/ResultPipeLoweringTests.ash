import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.Types
export (
    value runResultPipeLoweringTests,
)

let parsedProgram source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let loweredResult source =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { program = Some(_program), error = None } as result -> result
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("program lowering failed: " + text))
        | _ -> test.fail("program lowering produced no program")

let dumpSource source =
    match loweredResult(source) with
        | CoreLoweringResult { program = Some(program) } -> formatIr(program)(LoweredIr)(None)
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

let recursive countLinesContaining (fragment: Str) (lines: List(Str)) =
    match lines with
        | [] -> 0
        | line :: rest ->
            if Ashes.Text.contains(line)(fragment)
            then 1 + countLinesContaining(fragment)(rest)
            else countLinesContaining(fragment)(rest)

let checkSource = "let check (s: Str) =\n    if Ashes.Text.byteLength(s) > 0\n    then Ok(s)\n    else Error(\"empty\")\n\n"

// The success pipe tests the left operand's tag against `Ok`, calls the mapper on the payload,
// rewraps the mapped value in `Ok` (the second `Ok` cell after `check`'s own), and lets an
// `Error` operand through unchanged.
let expectSuccessPipeRewrapsTheMappedValue unit =
    checkSource + "match check(\"abc\") |?> (given (text) -> text) with\n    | Ok(text) -> text\n    | Error(message) -> message"
    |> dumpSource
    |> (given (lines) ->
        Unit
        |> (given (_) -> expectContains("GetAdtTag")(lines))
        |> (given (_) -> expectContains("JumpIfFalse           CondTemp=")(lines))
        |> (given (_) -> expectContains("Target=result_error_")(lines))
        |> (given (_) -> expectContains("CallClosure")(lines))
        |> (given (_) -> expectContains("Jump                  Target=result_end_")(lines))
        |> (given (_) -> expectContains("result_error_")(lines))
        |> (given (_) -> expectContains("result_end_")(lines))
        |> (given (_) ->
            lines
            |> countLinesContaining(" Tag=0 FieldCount=1")
            |> test.assertEqual(2)))

// A mapper that itself returns a `Result` makes the pipe a flat map typed at the mapper's own
// `Result`, not a nested one, and allocates no `Ok` cell of its own.
let expectFlatMapPipeKeepsTheMapperResultType unit =
    match loweredResult(checkSource + "check(\"abc\") |?> check") with
        | CoreLoweringResult { program = Some(program), semanticType = SemNamed(_id, "Result", SemString :: SemString :: []) } ->
            None
            |> formatIr(program)(LoweredIr)
            |> countLinesContaining(" Tag=0 FieldCount=1")
            |> test.assertEqual(1)
        | CoreLoweringResult { semanticType = other } -> test.fail("expected Result(Str, Str), got " + Ashes.Trait.Show.show(other))

// A mapper whose parameter type disagrees with the success payload is a type mismatch.
let expectMapperParameterMismatchIsRejected unit =
    checkSource + "check(\"abc\") |?> (given (n: Int) -> n)"
    |> loweringErrorFor
    |> (given (error) ->
        match error with
            | CoreCallTypeMismatch(_mismatch, _site) -> Unit
            | other -> test.fail("expected CoreCallTypeMismatch, got " + Ashes.Trait.Show.show(other)))

let runResultPipeLoweringTests unit =
    unit
    |> expectSuccessPipeRewrapsTheMappedValue
    |> expectFlatMapPipeKeepsTheMapperResultType
    |> expectMapperParameterMismatchIsRejected
