import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrOptimizer
let recursive countInstructions (functions: List(IrFunction)) (total: Int) =
    match functions with
        | [] -> total
        | IrFunction { instructions = instructions } :: rest -> countInstructions(rest)(total + Ashes.Collection.List.length(instructions))

// Optimizes the same lowered program once per round. Nothing of a round outlives it, so whatever
// the reference-counted heap still holds at exit, beyond one round's worth, leaked in the optimizer.
let recursive optimizeRounds (rounds: Int) (program: IrProgram) (total: Int) =
    if rounds == 0
    then total
    else
        match optimizeIrProgram(program) with
            | IrProgram { functions = functions } -> optimizeRounds(rounds - 1)(program)(total + countInstructions(functions)(0))

let run (path: Str) (rounds: Int) =
    match Ashes.IO.File.readText(path) with
        | Error(message) -> "could not read " + path + ": " + message
        | Ok(source) ->
            match parseProgram(source) with
                | ProgramParseResult { program = program, diagnostics = [] } ->
                    match lowerCoreProgramWithSource(path)(source)(program) with
                        | CoreLoweringResult { program = Some(lowered), error = None } ->
                            0
                            |> optimizeRounds(rounds)(lowered)
                            |> Ashes.Text.fromInt
                        | _ -> "lowering failed"
                | ProgramParseResult { diagnostics = _ } -> "parse failed"

// The round count is the length of the second argument, so no number parsing is needed.
match Ashes.IO.args with
    | path :: unary :: [] ->
        unary
        |> Ashes.Text.byteLength
        |> run(path)
        |> Ashes.IO.print
    | _ -> Ashes.IO.print("usage: optloop <source> <one character per round>")
