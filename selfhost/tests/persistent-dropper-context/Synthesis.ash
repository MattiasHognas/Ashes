import ProbeDroppers
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
let box argument = SemNamed(0)("Box")([argument])

let definition =
    ConstructorInferenceDefinition(name = "Box", scheme = TypeScheme(quantified = [(7, "a")], body = SemFunction(SemVariable(7))(box(SemVariable(7)))(None), constraints = []), fieldNames = [])

let target =
    SemString
    |> box
    |> box
    |> box

let carry (result: InlineReleaseSynthesis) (types: DropperTypes) = CONTEXT_CARRY

let recursive run rounds types checksum =
    if rounds == 0
    then
        checksum
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    else
        let result = synthesizeChildDrop(0)(target)(types)(emptyDropperLabelCache)(1)(0)(0)(0)
        in
            run(rounds - 1)(carry(result)(types))(checksum + Ashes.Collection.List.length(result.instructions) + Ashes.Collection.List.length(result.functions))

let recursive printLines (lines: List(Str)) =
    match lines with
        | [] -> Unit
        | line :: rest ->
            line
            |> Ashes.IO.print
            |> (given (_) -> printLines(rest))

let recursive printFunctions (functions: List(IrFunction)) =
    match functions with
        | [] -> Unit
        | function :: rest ->
            function.label
            |> Ashes.IO.print
            |> (given (_) ->
                function.instructions
                |> Ashes.Collection.List.map(formatIrInstruction)
                |> printLines)
            |> (given (_) -> printFunctions(rest))

match Ashes.IO.args with
    | "oracle" :: [] ->
        let result =
            synthesizeChildDrop(0)(target)(prepareDropperTypes([definition]))(emptyDropperLabelCache)(1)(0)(0)(0)
        in
            result.instructions
            |> Ashes.Collection.List.map(given (instruction: IrInstructionKind) -> formatIrInstruction(IrInstruction(instruction = instruction, location = None)))
            |> printLines
            |> (given (_) -> printFunctions(result.functions))
    | "1000" :: [] ->
        run(1000)(prepareDropperTypes([definition]))(0)
    | "10000" :: [] ->
        run(10000)(prepareDropperTypes([definition]))(0)
    | "100000" :: [] ->
        run(100000)(prepareDropperTypes([definition]))(0)
    | _ -> Ashes.IO.print("expected oracle/1000/10000/100000")
