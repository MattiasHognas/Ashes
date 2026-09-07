import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
export (
    value runFunctionFieldLoweringTests,
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

// A record whose only field is a function stores the closure word in a tagless cell, and a
// pattern binding of that field is called through the closure calling convention.
let expectFunctionTypedRecordFieldLowers unit =
    "type Box =\n    | reader: Int -> Str\n\nlet box (s: Str) =\n    Box(reader = given (u: Int) -> s)\n\nmatch box(\"abc\") with\n    | Box { reader = reader } -> reader(0)"
    |> dumpSource
    |> (given (lines) ->
        Unit
        |> (given (_) -> expectContains("MakeClosure           Target=2 FuncLabel=lambda_1 EnvPtrTemp=0 EnvSizeBytes=8")(lines))
        |> (given (_) -> expectContains("AllocAdt              Target=3 Tag=0 FieldCount=1 Tagless=true")(lines))
        |> (given (_) -> expectContains("SetAdtField           Ptr=3 FieldIndex=0 Source=2 Tagless=true")(lines))
        |> (given (_) -> expectContains("GetAdtField")(lines))
        |> (given (_) -> expectContains("CallClosure")(lines)))

// A positional constructor field of function type resolves the same way as a named one.
let expectFunctionTypedPositionalFieldLowers unit =
    "type Step =\n    | Step(Int -> Int)\n\nmatch Step(given (x) -> x + 1) with\n    | Step(f) -> f(41)"
    |> dumpSource
    |> (given (lines) ->
        Unit
        |> (given (_) -> expectContains("MakeClosure")(lines))
        |> (given (_) -> expectContains("CallClosure")(lines)))

// A function field over the type's own parameter quantifies the parameter like any other field.
let expectFunctionFieldOverTypeParameterLowers unit =
    "type Mapper(a) =\n    | Mapper(a -> Int)\n\nmatch Mapper(given (s) -> Ashes.Text.byteLength(s)) with\n    | Mapper(f) -> f(\"abcd\")"
    |> dumpSource
    |> (given (lines) -> expectContains("CallClosure")(lines))

// A field type the resolver still cannot express is rejected with the declaration diagnostic.
let expectFunctionFieldWithCapabilityRowIsRejected unit =
    "capability Log =\n    | write : Str -> Unit\n\ntype Box =\n    | run: Int -> Str needs {Log}\n\n1"
    |> loweringErrorFor
    |> (given (error) ->
        match error with
            | UnsupportedTypeDeclaration(_message) -> Unit
            | other -> test.fail("expected UnsupportedTypeDeclaration, got " + Ashes.Trait.Show.show(other)))

let runFunctionFieldLoweringTests unit =
    unit
    |> expectFunctionTypedRecordFieldLowers
    |> expectFunctionTypedPositionalFieldLowers
    |> expectFunctionFieldOverTypeParameterLowers
    |> expectFunctionFieldWithCapabilityRowIsRejected
