import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.StandardTraits
import AshesCompiler.Semantics.CoreConstrainedSpecialization
export (
    value runCoreConstrainedSpecializationInferenceTests,
)

let require condition message =
    if condition
    then Unit
    else Ashes.IO.panic(message)

let checkPureCall (scheme: TypeScheme) pureScheme =
    match 1000
    |> typeVariableSupplyFrom
    |> instantiate(pureScheme) with
        | InstantiationResult { semanticType = functionType, supply = supply } ->
            let environment =
                Unit
                |> standardTraitEnvironment
                |> addTypeBinding("private")(TypeScheme(quantified = [], body = functionType, constraints = []))
            in
                match inferExpressionFrom(ExprCall(ExprCall(ExprVar("private"))(ExprInt(1))(false)(callArgumentsInline))(ExprInt(2))(false)(callArgumentsInline))(environment)([])(supply) with
                    | TypeInferenceResult { substitution = substitution, error = None } ->
                        let resolved = applySubstitution(substitution)(functionType)
                        in
                            match concreteConstrainedArguments(2)(resolved) with
                                | Some(arguments) ->
                                    match pinProvenConcreteConstrainedFunction(scheme)(arguments)(resolved) with
                                        | Some(pinned) -> require(concreteConstrainedArguments(2)(pinned) == Some([SemInt, SemInt]))("inferred pinned arguments")
                                        | None -> Ashes.IO.panic("inferred pure scheme pinning rejected")
                                | None -> Ashes.IO.panic("inferred pure call admission rejected: " + Ashes.Trait.Show.show(resolved))
                    | _ -> Ashes.IO.panic("inferred concrete call failed")

let checkCall (scheme: TypeScheme) =
    match preparePureConcreteScheme(scheme)(2) with
        | Some(pureScheme) -> checkPureCall(scheme)(pureScheme)
        | None -> Ashes.IO.panic("inferred source pure instantiation rejected")

let runCoreConstrainedSpecializationInferenceTests unit =
    (let source =
        ExprLambda("left")(ExprLambda("right")(ExprEqual(ExprVar("left"))(ExprVar("right")))(None))(None)
    in
        let inferred =
            100
            |> typeVariableSupplyFrom
            |> inferExpressionFrom(source)(standardTraitEnvironment(Unit))([])
        in
            let checked =
                match inferred with
                    | TypeInferenceResult { semanticType = semanticType, substitution = substitution, constraints = constraints, error = None } ->
                        constraints
                        |> generalize([])(applySubstitution(substitution)(semanticType))
                        |> checkCall
                    | _ -> Ashes.IO.panic("source lambda inference failed")
            in
                let rowScheme = TypeScheme(quantified = [(10, "a"), (11, "row")], body = SemFunction(SemVariable(10))(SemBool)(Some(SemVariable(11))), constraints = [])
                in
                    Unit
                    |> (given (_) -> require(preparePureConcreteScheme((rowScheme with quantified = [(10, "a")]))(1) == None)("captured source row instantiated"))
                    |> (given (_) ->
                        require(preparePureConcreteScheme((rowScheme with body = SemFunction(SemVariable(10))(SemBool)(None
                        |> SemRow([SemCapability("IO")([])])
                        |> Some)))(1) == None)("effectful source row instantiated"))
                    |> (given (_) ->
                        require(pinProvenConcreteConstrainedFunction((rowScheme with quantified = [(10, "a"), (11, "row"), (12, "result")], body = SemFunction(SemVariable(10))(SemVariable(12))(Some(SemVariable(11)))))([SemInt])(SemFunction(SemInt)(SemBool)(None)) == None)("independent result pinned from call"))
                    |> (given (_) ->
                        require(pinProvenConcreteConstrainedFunction(rowScheme)([SemInt])(None
                        |> SemRow([])
                        |> Some
                        |> SemFunction(SemInt)(SemBool)) == Some(None
                        |> SemRow([])
                        |> Some
                        |> SemFunction(SemInt)(SemBool)))("proven quantified empty row rejected"))
                    |> (given (_) ->
                        require(pinProvenConcreteConstrainedFunction(rowScheme)([SemInt])(SemFunction(SemInt)(SemBool)(Some(SemVariable(12)))) == None)("unproved row closed"))
                    |> (given (_) ->
                        require(pinProvenConcreteConstrainedFunction(rowScheme)([SemInt])(None
                        |> SemRow([SemCapability("IO")([])])
                        |> Some
                        |> SemFunction(SemInt)(SemBool)) == None)("effectful row erased"))
                    |> (given (_) ->
                        require(pinProvenConcreteConstrainedFunction((rowScheme with quantified = [(10, "a")]))([SemInt])(SemFunction(SemInt)(SemBool)(None)) == None)("nonquantified source row closed"))
                    |> (given (_) ->
                        require(pinProvenConcreteConstrainedFunction(rowScheme)([SemInt])(SemFunction(SemString)(SemBool)(None)) == None)("unrelated inferred parameters accepted"))
                    |> (given (_) ->
                        require(pinProvenConcreteConstrainedFunction(rowScheme)([SemInt])(SemFunction(SemInt)(SemInt)(None)) == None)("unrelated inferred result accepted"))
                    |> (given (_) ->
                        require(concreteConstrainedArguments(1)(SemFunction(SemInt)(SemBool)(Some(SemVariable(12)))) == None)("open row admitted")))
