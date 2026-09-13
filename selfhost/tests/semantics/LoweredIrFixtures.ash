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
    value withFunctionLocalLabels,
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

// A label definition line is indented two spaces (an instruction four) and ends in `:`.
let labelDefinition (line: Str) =
    (let trimmed = Ashes.Text.trim(line)
    in
        if Ashes.Text.startsWith(line)("  ") && Ashes.Text.startsWith(line)("    ") == false && Ashes.Text.length(trimmed) > 1 && Ashes.Text.substring(trimmed)(Ashes.Text.length(trimmed) - 1)(1) == ":"
        then
            Ashes.Text.length(trimmed) - 1
            |> Ashes.Text.take(trimmed)
            |> Some
        else None)

let recursive dropLast (parts: List(Str)) =
    match parts with
        | [] -> []
        | _last :: [] -> []
        | head :: rest -> head :: dropLast(rest)

// `rc_normalize_list_13` -> `rc_normalize_list`: the program-wide number is the last segment.
let labelBase (name: Str) =
    "_"
    |> Ashes.Text.split(name)
    |> dropLast
    |> Ashes.Text.join("_")

let recursive collectLabelRenames (lines: List(Str)) (index: Int) (renames: List((Str, Str))) =
    match lines with
        | [] -> Ashes.Collection.List.reverse(renames)
        | line :: rest ->
            match labelDefinition(line) with
                | None -> collectLabelRenames(rest)(index)(renames)
                | Some(name) -> collectLabelRenames(rest)(index + 1)((name, labelBase(name) + "_" + Ashes.Text.fromInt(index)) :: renames)

let recursive renameLabelToken (renames: List((Str, Str))) (token: Str) =
    match renames with
        | [] -> token
        | (old, new) :: rest ->
            if token == old + ":"
            then new + ":"
            else
                if token == "Target=" + old
                then "Target=" + new
                else renameLabelToken(rest)(token)

let renameLabels (renames: List((Str, Str))) (line: Str) =
    " "
    |> Ashes.Text.split(line)
    |> Ashes.Collection.List.map(renameLabelToken(renames))
    |> Ashes.Text.join(" ")

// Label numbers are allocated program-wide in lowering order, so a function compared on its own
// inherits the count of every label the functions before it allocated. Where one of those
// functions is a known cross-compiler difference (the self-hosted lowering applies
// tail-modulo-constructor to a generic `mapAll`, which stage 0 declines; OPT-63), the compared
// function's labels shift while its instructions stay identical. Renumbering each label by the
// order of its definition within the function keeps the comparison about the instructions.
let withFunctionLocalLabels (lines: List(Str)) =
    (let renames = collectLabelRenames(lines)(0)([])
    in
        Ashes.Collection.List.map(renameLabels(renames))(lines))

let expectSameLines (label: Str) (expected: List(Str)) (actual: List(Str)) =
    if expected == actual
    then Unit
    else
        test.fail(
            label + "\nexpected:\n" + Ashes.Text.join("\n")(expected) + "\nactual:\n" + Ashes.Text.join("\n")(actual)
        )
