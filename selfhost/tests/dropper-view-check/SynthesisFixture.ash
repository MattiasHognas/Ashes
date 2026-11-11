import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
export (
    value synthesisRounds,
)

let definitions n =
    (let innerName = "Inner" + Ashes.Text.fromInt(n)
    in
        let outerName = "Outer" + Ashes.Text.fromInt(n)
        in
            let inner = SemNamed(0)(innerName)([])
            in
                let outer = SemNamed(1)(outerName)([])
                in
                    ([ConstructorInferenceDefinition(name = innerName, scheme = TypeScheme(quantified = [], body = SemFunction(SemList(SemString))(inner)(None), constraints = []), fieldNames = ["items"]), ConstructorInferenceDefinition(name = outerName, scheme = TypeScheme(quantified = [], body = SemFunction(inner)(SemFunction(SemList(inner))(outer)(None))(None), constraints = []), fieldNames = ["value", "values"])], outer))

let query n =
    match definitions(n) with
        | (constructors, outer) ->
            match synthesizeStructuralOwnerDropper(SemTuple([outer, SemList(outer)]))(prepareDropperTypes(constructors))(emptyDropperLabelCache)(0)(0) with
                | DropperSynthesis { functions = [] } -> false
                | _ -> true

let recursive synthesisRounds n =
    if n == 0
    then Ashes.IO.print("done")
    else
        if query(n)
        then synthesisRounds(n - 1)
        else Ashes.IO.panic("missing dropper")
