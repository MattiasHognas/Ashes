import AshesCompiler.Cli.DiagnosticIrDump
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.IrInstructions
let function label value = IrFunction(label = label, instructions = [IrInstruction(instruction = LoadConstInt(0)(value), location = None), IrInstruction(instruction = Return(0), location = None)], localCount = 0, tempCount = 1, hasEnvAndArgParams = false, coroutine = None, localNames = [], localTypes = [], origin = None, lifetimesPlaced = false)

let selected value = function("selected")(value) with origin = Some(IrFunctionOrigin(generatedLabel = "selected", originKind = SourceFunctionOriginKind, sourceOrigin = Some(SourceFunctionOrigin(functionSourceName = "SourceName", functionQualifiedName = Some("Module.SourceName"), declarationLocation = None, declarationOffset = 0)), parentGeneratedLabel = Some("ParentLabel"), compilerOwner = None, stableDiscriminator = None, generationLocation = None))

let program value = IrProgram(entryFunction = function("main")(value), functions = [selected(value + 1), function("other")(value + 2)], stringLiterals = [IrStringLiteral(label = "literal", value = Ashes.Text.fromInt(value))], externalFunctions = [], externalOpaqueTypes = [], usesPrintInt = false, usesPrintStr = false, usesPrintBool = false, usesConcatStr = false, usesClosures = false, usesAsync = false, capabilityHandlerGlobals = 0, traitEvidence = emptyTraitEvidenceAnnotations)

let literalValue (program: IrProgram) =
    match program.stringLiterals with
        | literal :: [] -> literal.value
        | _ -> "missing"

let render program =
    None
    |> formatIr(program)(LoweredIr)
    |> Ashes.Text.join("\n")

let reuseAfterHook unit =
    (let lowered = program(10)
    in
        let optimized = program(20)
        in
            let result = writeDiagnosticIrDump(lowered)(optimized)
            in
                if render(lowered) == render(program(10)) && render(optimized) == render(program(20)) && literalValue(lowered) == "10" && literalValue(optimized) == "20"
                then
                    match result with
                        | Ok(_) -> Ashes.IO.print("ok")
                        | Error(message) -> Ashes.IO.print(message)
                else Ashes.IO.panic("diagnostic hook changed or released its IR input"))

match Ashes.IO.args with
    | "oracle-lowered" :: filter :: [] ->
        (if filter == ""
        then None
        else Some(filter))
        |> formatIr(program(10))(LoweredIr)
        |> Ashes.Text.join("\n")
        |> Ashes.IO.print
    | "oracle-final" :: filter :: [] ->
        (if filter == ""
        then None
        else Some(filter))
        |> formatIr(program(20))(FinalIr)
        |> Ashes.Text.join("\n")
        |> Ashes.IO.print
    | _ -> reuseAfterHook(Unit)
