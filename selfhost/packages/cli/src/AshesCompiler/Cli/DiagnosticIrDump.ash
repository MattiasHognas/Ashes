// Opt-in development instrumentation; ordinary compilation never formats or writes IR.
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
export (
    value writeDiagnosticIrDump,
)

let diagnosticFilter unit =
    match Ashes.IO.Environment.get("ASHES_DIAGNOSTIC_IR_FILTER") with
        | Ok(Some("")) -> None
        | Ok(filter) -> filter
        | Error(_message) -> None

let writeDiagnosticStage path stage filter program =
    match Ashes.IO.File.writeText(path)(Ashes.Text.join("\n")(formatIr(program)(stage)(filter)) + "\n") with
        | Ok(_) -> Ok(Unit)
        | Error(message) -> Error("Could not write diagnostic IR " + path + ": " + message)

let writeDiagnosticIrDump (lowered: IrProgram) (optimized: IrProgram) =
    match Ashes.IO.Environment.get("ASHES_DIAGNOSTIC_IR_PREFIX") with
        | Ok(Some(prefix)) when prefix != "" ->
            let filter = diagnosticFilter(Unit)
            in
                match writeDiagnosticStage(prefix + ".lowered.ir")(LoweredIr)(filter)(lowered) with
                    | Error(message) -> Error(message)
                    | Ok(_) -> writeDiagnosticStage(prefix + ".final.ir")(FinalIr)(filter)(optimized)
        | _ -> Ok(Unit)
