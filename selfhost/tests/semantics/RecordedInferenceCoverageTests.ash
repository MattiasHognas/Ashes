import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.NodeIdentity
import AshesCompiler.Semantics.NodeTypeTable
import AshesCompiler.Semantics.ProgramInference
export (
    value runRecordedInferenceCoverageTests,
)

let recursive bindingType name (bindings: List((Str, TypeScheme))) =
    match bindings with
        | [] -> None
        | (candidate, scheme) :: rest ->
            if candidate == name
            then Some(scheme.body)
            else bindingType(name)(rest)

let checkArm (table: NodeTypeTable) found _node _span expression =
    match expression with
        | ExprHandle(_body, (_capability, "examine", _patterns, arm) :: _) ->
            let bindings =
                nodeBindingsAt(nodeIdentityOf(arm))(table)
            in
                match (bindingType("Ready")(bindings), bindingType("text")(bindings), bindingType("resume")(bindings)) with
                    | (None, Some(SemString), Some(SemFunction(SemString, SemString, _row))) -> true
                    | _ -> Ashes.IO.panic("handler arm must bind its tuple variable and continuation, excluding the nullary constructor")
        | _ -> found

let checkHandlerBindings unit =
    (let source = "type Flag = | Ready\ncapability Inspect = | examine : (Flag, Str) -> Str\nhandle perform Inspect.examine((Ready, \"value\")) with | Inspect.examine((Ready, text)) -> resume(text) | return(result) -> result"
    in
        match parseProgram(source) with
            | ProgramParseResult { program = parsed, diagnostics = [] } ->
                match identifyProgram(0)(parsed) with
                    | (program, count) ->
                        match inferProgram(program) with
                            | ProgramInferenceResult { nodeTypes = table, error = None } ->
                                if table.typeCount == count && foldProgramNodes(checkArm(table))(false)(program)
                                then Unit
                                else Ashes.IO.panic("handler expression coverage incomplete")
                            | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("handler inference failed: " + Ashes.Trait.Show.show(error))
            | _ -> Ashes.IO.panic("handler inference fixture did not parse"))

let runRecordedInferenceCoverageTests unit = checkHandlerBindings(unit)
