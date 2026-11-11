import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.StructuralCopiers
import AshesCompiler.Semantics.ToSpaceCopiers
export (
    value runCopierFixture,
)

let named name = SemNamed(1)(name)([])

let recursive curried fields result =
    match fields with
        | [] -> result
        | head :: rest ->
            SemFunction(head)(curried(rest)(result))(None)

let def name fields result = ConstructorInferenceDefinition(name = name, scheme = TypeScheme(quantified = [], body = curried(fields)(result), constraints = []), fieldNames = [])

let definitions =
    ["Record"
    |> named
    |> def("Record")([SemInt, SemString]), "Tree"
    |> named
    |> def("Leaf")([]), "Tree"
    |> named
    |> def("Node")([SemInt, named("Tree")])]

let recursive dumpKinds (kinds: List(IrInstructionKind)) =
    match kinds with
        | [] -> Unit
        | kind :: rest ->
            IrInstruction(instruction = kind, location = None)
            |> formatIrInstruction
            |> Ashes.IO.print
            |> (given (_) -> dumpKinds(rest))

let recursive dumpInstructions (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> Unit
        | instruction :: rest ->
            instruction
            |> formatIrInstruction
            |> Ashes.IO.print
            |> (given (_) -> dumpInstructions(rest))

let recursive dumpFunctions (functions: List(IrFunction)) =
    match functions with
        | [] -> Unit
        | function :: rest ->
            function.label + " " + Ashes.Text.fromInt(function.localCount) + " " + Ashes.Text.fromInt(function.tempCount)
            |> Ashes.IO.print
            |> (given (_) -> dumpInstructions(function.instructions))
            |> (given (_) -> dumpFunctions(rest))

let dump result =
    match result with
        | (InlineReleaseSynthesis { instructions = instructions, functions = functions, nextTemp = temp, nextLocal = local, nextLambdaId = lambda, nextLabelId = label }, resultTemp) ->
            Ashes.Text.fromInt(temp) + " " + Ashes.Text.fromInt(local) + " " + Ashes.Text.fromInt(lambda) + " " + Ashes.Text.fromInt(label) + " " + Ashes.Text.fromInt(resultTemp)
            |> Ashes.IO.print
            |> (given (_) -> dumpKinds(instructions))
            |> (given (_) -> dumpFunctions(functions))

let recursive cases types prepared =
    match types with
        | [] -> Unit
        | semanticType :: rest ->
            IndependentClone
            |> synthesizeDeepCopy(0)(semanticType)(prepared)(emptyDropperLabelCache)(1)(0)(1)(10)
            |> dump
            |> (given (_) ->
                10
                |> synthesizeToSpaceCopy(0)(semanticType)(prepared)(emptyDropperLabelCache)(1)(0)(1)
                |> dump)
            |> (given (_) -> cases(rest)(prepared))

let runCopierFixture unit =
    definitions
    |> prepareDropperTypes
    |> cases([named("Record"), named("Tree"), "Record"
    |> named
    |> SemList, SemTuple([SemString, SemList(SemInt), named("Tree")])])
