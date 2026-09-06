import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.DiagnosticSerialization
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.LoweringDiagnostics
let fixturePath root name extension = root + "/" + name + extension

let readFixture path =
    match Ashes.IO.File.readText(path) with
        | Ok(value) -> value
        | Error(message) -> test.fail("could not read parity fixture " + path + ": " + message)

// Lowering stops at its first error, so a fixture pins the one diagnostic stage 0 reports for the
// program: its code, its message text, and its span.
let loweredDiagnostics name source program =
    match lowerCoreProgramWithSource(name + ".ash")(source)(program) with
        | CoreLoweringResult { error = Some(error) } ->
            match loweringErrorDiagnostic(error) with
                | Some(entry) -> [entry]
                | None -> test.fail("lowering of " + name + " failed without a stage-0 diagnostic: " + Ashes.Trait.Show.show(error))
        | _ -> []

let checkFixture root name =
    (let source =
        ".source"
        |> fixturePath(root)(name)
        |> readFixture
    in
        let expected =
            ".diagnostics"
            |> fixturePath(root)(name)
            |> readFixture
        in
            match parseProgram(source) with
                | ProgramParseResult { program = program, diagnostics = [] } ->
                    let actual =
                        program
                        |> loweredDiagnostics(name)(source)
                        |> serializeDiagnostics(Ashes.Collection.List.length(program.items))
                    in
                        if actual == expected
                        then Unit
                        else
                            test.fail(
                                "diagnostic parity mismatch for " + name + "\nexpected:\n" + expected + "actual:\n" + actual
                            )
                | ProgramParseResult { diagnostics = diagnostics } -> test.fail(name + " should parse cleanly: " + Ashes.Trait.Show.show(diagnostics)))

match Ashes.IO.args with
    | root :: [] ->
        Unit
        |> (given (_) -> checkFixture(root)("call_argument_mismatch"))
        |> (given (_) -> checkFixture(root)("tail_self_call_argument_mismatch"))
        |> (given (_) -> Ashes.IO.print("all self-hosted semantic diagnostic parity fixtures passed"))
    | _ -> Ashes.IO.panic("usage: semantics-diagnostic-parity <fixture-directory>")
