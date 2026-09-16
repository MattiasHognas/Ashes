// Closure environment scalarization generates one callee variant per (callee, capture count) and
// memoizes it. Two different curried callees applied to the same argument shape must keep two
// variants: folding them would make a call to one run the other's body. These checks lower and
// optimize such a program and assert the two call sites keep distinct targets.
//
// This is CG-20's reproduction. It used to fail because the memo was keyed by a string built from
// the callee's label and capture count: that key was released at the scope exit of
// `getOrCreateScalarEnvVariant` while the returned memo still held it, the next key's allocation
// reused the cell, and the second lookup answered with the first entry. The memo now holds the
// label and the count side by side and allocates no key at all.
import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrOptimizer
import AshesCompiler.Semantics.IrText
import LoweredIrFixtures.functionLines
export (
    value runScalarEnvVariantTests,
)

let twoCurriedCalleesSource = "let recursive first xs acc =\n    match xs with\n        | [] -> acc\n        | _ :: rest -> first(rest)(acc + 1)\n\nlet recursive second xs acc =\n    match xs with\n        | [] -> acc\n        | _ :: rest -> second(rest)(acc + 10)\n\nlet items = [\"a\", \"b\", \"c\"]\n\nAshes.IO.print(Ashes.Text.fromInt(first(items)(0)) + \"|\" + Ashes.Text.fromInt(second(items)(0)))\n"

let parsedProgram (source: Str) =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let optimizedLines (source: Str) =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { program = Some(program), error = None } ->
            formatIr(optimizeIrProgram(program))(FinalIr)(None)
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("program lowering failed: " + text))
        | _ -> test.fail("program lowering produced no program")

// The `FuncLabel=` operand of every line that carries one.
let recursive funcLabelOf (tokens: List(Str)) =
    match tokens with
        | [] -> None
        | token :: rest ->
            match Ashes.Text.split(token)("=") with
                | name :: value :: [] ->
                    if name == "FuncLabel"
                    then Some(value)
                    else funcLabelOf(rest)
                | _ -> funcLabelOf(rest)

let recursive callTargets (lines: List(Str)) (acc: List(Str)) =
    match lines with
        | [] -> Ashes.Collection.List.reverse(acc)
        | line :: rest ->
            if Ashes.Text.contains(line)("CallKnown")
            then
                match " "
                |> Ashes.Text.split(line)
                |> funcLabelOf with
                    | Some(label) -> callTargets(rest)(label :: acc)
                    | None -> callTargets(rest)(acc)
            else callTargets(rest)(acc)

let recursive listHas (needle: Str) (labels: List(Str)) =
    match labels with
        | [] -> false
        | label :: rest -> label == needle || listHas(needle)(rest)

let recursive hasDuplicate (seen: List(Str)) (remaining: List(Str)) =
    match remaining with
        | [] -> None
        | label :: rest ->
            if listHas(label)(seen)
            then Some(label)
            else hasDuplicate(label :: seen)(rest)

// Both applications in the trailing expression are saturated calls to different functions, so no
// two of the entry's direct calls may share a target.
let expectDistinctScalarEnvVariants unit =
    match twoCurriedCalleesSource
    |> optimizedLines
    |> functionLines("[ProgramEntry]")
    |> (given (lines: List(Str)) -> callTargets(lines)([])) with
        | targets ->
            match hasDuplicate([])(targets) with
                | None -> Unit
                | Some(label) ->
                    test.fail(
                        "two of the entry's direct calls share the target " + label + ", so one callee runs the other's body: " + Ashes.Trait.Show.show(targets)
                    )

let runScalarEnvVariantTests unit =
    Unit
    |> expectDistinctScalarEnvVariants
    |> (given (_) -> Ashes.IO.print("scalar env variant tests passed"))
