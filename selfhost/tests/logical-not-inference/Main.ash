import CoreLogicalNotInferenceTests
import CoreOrderedInferenceTests
import AshesCompiler.Frontend.Parser
import AshesCompiler.Semantics.CoreLowering
let report result =
    match result with
        | CoreLoweringResult { error = Some(error) } -> Ashes.IO.print("ERROR " + Ashes.Trait.Show.show(error))
        | CoreLoweringResult { semanticType = semanticType, error = None } -> Ashes.IO.print("OK " + Ashes.Trait.Show.show(semanticType))

let read path =
    match Ashes.IO.File.readText(path) with
        | Ok(source) -> source
        | Error(message) -> Ashes.IO.panic(message)

match Ashes.IO.args with
    | [] ->
        Unit
        |> runCoreLogicalNotInferenceTests
        |> (given (_) -> Ashes.IO.print("logical-not inference passed"))
        |> runCoreOrderedInferenceTests
        |> (given (_) -> Ashes.IO.print("ordered inference passed"))
    | "program" :: path :: [] ->
        match path
        |> read
        |> parseProgram with
            | ProgramParseResult { program = program, diagnostics = [] } ->
                program
                |> lowerCoreProgram
                |> report
            | _ -> Ashes.IO.panic("program did not parse")
    | "expression" :: path :: [] ->
        match path
        |> read
        |> parseExpression with
            | ExpressionParseResult { expression = expression, diagnostics = [] } ->
                expression
                |> lowerCoreExpression
                |> report
            | _ -> Ashes.IO.panic("expression did not parse")
    | _ -> Ashes.IO.panic("expected no arguments or program/expression path")
