import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOptimizer

let instruction (kind: IrInstructionKind) = IrInstruction(instruction = kind, location = None)

let function (label: Str) (instructions: List(IrInstruction)) =
    IrFunction(
        label = label,
        instructions = instructions,
        localCount = 2,
        tempCount = 4,
        hasEnvAndArgParams = true,
        coroutine = None,
        localNames = [],
        localTypes = [],
        origin = None,
        lifetimesPlaced = false
    )

let stage (index: Int) =
    function("lambda_" + Ashes.Text.fromInt(index))([
        instruction(MakeClosure(1, "lambda_" + Ashes.Text.fromInt(index + 1), 0, 8, false, false, false, false)),
        instruction(Return(1))
    ])

let recursive stages (index: Int) (count: Int) =
    if index >= count
    then []
    else stage(index) :: stages(index + 1)(count)

let entry =
    function("main")([
        instruction(CallKnown(1, "lambda_0", 0, 0, -1, false)),
        instruction(CallClosure(2, 1, 0, -1)),
        instruction(Return(2))
    ])

let program (count: Int) =
    IrProgram(
        entryFunction = entry,
        functions = stages(0)(count),
        stringLiterals = [],
        externalFunctions = [],
        externalOpaqueTypes = [],
        usesPrintInt = false,
        usesPrintStr = false,
        usesPrintBool = false,
        usesConcatStr = false,
        usesClosures = true,
        usesAsync = false,
        capabilityHandlerGlobals = 0,
        traitEvidence = emptyTraitEvidenceAnnotations
    )

match Ashes.IO.args with
    | count :: [] ->
        match Ashes.Text.toInt(count) with
            | Some(n) ->
                match optimizeIrProgram(program(n)) with
                    | IrProgram { functions = functions } -> Ashes.IO.print(Ashes.Collection.List.length(functions))
            | None -> Ashes.IO.print("bad count")
    | _ -> Ashes.IO.print("usage: opt409 <count>")
