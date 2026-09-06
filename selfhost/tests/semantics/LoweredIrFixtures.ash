// The lowered-IR parity fixtures (`selfhost/parity/semantics/lowered-ir`, read relative to the
// repository root like the backend suite's shared programs) for the tests that compare one
// function of a program with stage 0 line for line, where the whole program does not yet match
// byte for byte.
import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
export (
    value loweredFixtureLines,
    value stageZeroFixtureLines,
    value functionLines,
    value withoutLocations,
    value expectSameLines,
)

let fixtureRoot = "selfhost/parity/semantics/lowered-ir"

let readFixture (path: Str) =
    match Ashes.IO.File.readText(path) with
        | Ok(value) -> value
        | Error(message) -> test.fail("could not read " + path + ": " + message)

let parsedProgram (source: Str) =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

// The self-hosted lowering of the fixture's source, as IR text lines.
let loweredFixtureLines (name: Str) =
    (let source = readFixture(fixtureRoot + "/" + name + ".source")
    in
        match source
        |> parsedProgram
        |> lowerCoreProgramWithSource(name + ".ash")(source) with
            | CoreLoweringResult { program = Some(program), error = None } -> formatIr(program)(LoweredIr)(None)
            | CoreLoweringResult { error = Some(error) } -> test.fail("lowering failed for " + name + ": " + Ashes.Trait.Show.show(error))
            | _ -> test.fail("lowering produced no program for " + name))

// Stage 0's oracle for the fixture, as lines.
let stageZeroFixtureLines (name: Str) =
    Ashes.Text.split(readFixture(fixtureRoot + "/" + name + ".ir"))("\n")

let isFunctionHeader (line: Str) = Ashes.Text.startsWith(line)("function ")

// The lines of the function whose header carries `originText`, up to the next header, trailing
// blank lines dropped.
let recursive functionBody (lines: List(Str)) (collected: List(Str)) =
    match lines with
        | [] -> Ashes.Collection.List.reverse(collected)
        | line :: rest ->
            if isFunctionHeader(line)
            then Ashes.Collection.List.reverse(collected)
            else functionBody(rest)(line :: collected)

let recursive dropTrailingBlank (reversed: List(Str)) =
    match reversed with
        | "" :: rest -> dropTrailingBlank(rest)
        | _ -> reversed

let recursive functionLines (originText: Str) (lines: List(Str)) =
    match lines with
        | [] -> test.fail("no function with origin " + originText)
        | line :: rest ->
            if isFunctionHeader(line) && Ashes.Text.contains(line)(originText)
            then
                []
                |> functionBody(rest)
                |> Ashes.Collection.List.reverse
                |> dropTrailingBlank
                |> Ashes.Collection.List.reverse
            else functionLines(originText)(rest)

// The lines without the source location the IR text appends after three spaces.
let withoutLocations (lines: List(Str)) =
    Ashes.Collection.List.map(given (line: Str) ->
        match Ashes.Text.split(line)("   (") with
            | instruction :: _rest -> instruction
            | [] -> line)(lines)

let expectSameLines (label: Str) (expected: List(Str)) (actual: List(Str)) =
    if expected == actual
    then Unit
    else
        test.fail(
            label + "\nexpected:\n" + Ashes.Text.join("\n")(expected) + "\nactual:\n" + Ashes.Text.join("\n")(actual)
        )
