import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.NodeIdentity
import AshesCompiler.Semantics.NodeTypeTable
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.StandardTraits.standardTraitEnvironment
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

let checkComplement (table: NodeTypeTable) count node _span expression =
    match expression with
        | ExprBitwiseNot(operand) ->
            match (nodeOperatorAt(node)(table), nodeTypeAt(nodeIdentityOf(operand))(table)) with
                | (Some(NodeOperator { traitName = "BitwiseNot", instance = PrimitiveInstance, operandType = recorded }), Some(actual)) ->
                    if actual == recorded
                    then count + 1
                    else Ashes.IO.panic("complement operator recorded a different operand type")
                | _ -> Ashes.IO.panic("integer complement must record a primitive instance")
        | _ -> count

let checkPrimitiveComplements unit =
    match parseProgram("(~1, ~1u8, ~1u16, ~1u32, ~1u64)") with
        | ProgramParseResult { program = parsed, diagnostics = [] } ->
            match identifyProgram(0)(parsed) with
                | (program, count) ->
                    match inferProgramFromPackage("standalone")(standardTraitEnvironment(Unit))(program) with
                        | ProgramInferenceResult { nodeTypes = table, error = None } ->
                            if table.typeCount == count && foldProgramNodes(checkComplement(table))(0)(program) == 5
                            then Unit
                            else Ashes.IO.panic("complement expression coverage incomplete")
                        | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("complement inference failed: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("integer complement fixture did not parse")

let runRecordedInferenceCoverageTests unit =
    unit
    |> checkHandlerBindings
    |> checkPrimitiveComplements
