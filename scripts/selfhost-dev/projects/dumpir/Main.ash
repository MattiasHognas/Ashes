import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
let dump (path: Str) =
    match Ashes.IO.File.readText(path) with
        | Error(message) -> "could not read " + path + ": " + message
        | Ok(source) ->
            match parseProgram(source) with
                | ProgramParseResult { program = program, diagnostics = [] } ->
                    match lowerCoreProgramWithSource(path)(source)(program) with
                        | CoreLoweringResult { program = Some(lowered), error = None } ->
                            None
                            |> formatIr(lowered)(LoweredIr)
                            |> Ashes.Text.join("\n")
                        | CoreLoweringResult { error = Some(message) } -> "lowering failed: " + Ashes.Trait.Show.show(message)
                        | _ -> "lowering produced no program"
                | ProgramParseResult { diagnostics = _ } -> "parse failed"

match Ashes.IO.args with
    | path :: [] ->
        path
        |> dump
        |> Ashes.IO.print
    | _ -> Ashes.IO.print("usage: dumpir <source>")
