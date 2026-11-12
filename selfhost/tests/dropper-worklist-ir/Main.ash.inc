import ProbeDroppers
import CopierFixture
import StructuralDroppersTests
import DropperViewCacheTests
import DropperAdmissibilityProjectionTests
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrText
let expr = SemNamed(0)("Expr")([])

let box argument = SemNamed(1)("Box")([argument])

let rejected = SemNamed(2)("Rejected")([])

let recursive curried fields result =
    match fields with
        | [] -> result
        | head :: rest ->
            SemFunction(head)(curried(rest)(result))(None)

let definition name fields result = ConstructorInferenceDefinition(name = name, scheme = TypeScheme(quantified = [], body = curried(fields)(result), constraints = []), fieldNames = [])

let definitions =
    [
        definition("Text")([SemString])(expr),
        definition("Pair")([expr, expr])(expr),
        definition("Children")([SemList(expr)])(expr),
        ConstructorInferenceDefinition(name = "Box", scheme = TypeScheme(quantified = [(7, "a")], body = SemFunction(SemVariable(7))(box(SemVariable(7)))(None), constraints = []), fieldNames = []),
        definition("Rejected")([SemFunction(SemInt)(SemInt)(None)])(rejected)
    ]

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
            "function " + function.label + " locals=" + Ashes.Text.fromInt(function.localCount) + " temps=" + Ashes.Text.fromInt(function.tempCount)
            |> Ashes.IO.print
            |> (given (_) -> dumpInstructions(function.instructions))
            |> (given (_) -> dumpFunctions(rest))

let dump (result: InlineReleaseSynthesis) =
    "counters " + Ashes.Text.fromInt(result.nextTemp) + " " + Ashes.Text.fromInt(result.nextLocal) + " " + Ashes.Text.fromInt(result.nextLambdaId) + " " + Ashes.Text.fromInt(result.nextLabelId)
    |> Ashes.IO.print
    |> (given (_) ->
        result.instructions
        |> Ashes.Collection.List.map(given (kind: IrInstructionKind) -> IrInstruction(instruction = kind, location = None))
        |> dumpInstructions)
    |> (given (_) -> dumpFunctions(result.functions))

let recursive cases types prepared =
    match types with
        | [] -> Unit
        | semanticType :: rest ->
            "case " + formatSemanticType(semanticType)
            |> Ashes.IO.print
            |> (given (_) ->
                let result = synthesizeChildDrop(0)(semanticType)(prepared)(emptyDropperLabelCache)(1)(0)(1)(10)
                in
                    result
                    |> dump
                    |> (given (_) ->
                        prepared
                        |> withDropperQueryContext(result.queryContext)
                        |> cases(rest)))

let runIr unit =
    definitions
    |> prepareDropperTypes
    |> cases([expr, SemList(expr), SemTuple([expr, expr, SemList(expr)]), box(expr), expr
    |> box
    |> box, box(SemTuple([expr, SemList(expr)])), rejected, box(rejected), SemTuple([SemString, rejected])])
    |> (given (_) -> runCopierFixture(Unit))

match Ashes.IO.args with
    | "check" :: [] ->
        Unit
        |> runStructuralDroppersTests
        |> runDropperViewCacheTests
        |> runDropperAdmissibilityProjectionTests
    | "ir" :: [] -> runIr(Unit)
    | _ -> Ashes.IO.panic("expected check or ir")
