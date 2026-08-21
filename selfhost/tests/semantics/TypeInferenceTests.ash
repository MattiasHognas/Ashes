import Ashes.Test as test
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.TypeExpr
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
let expectTypeIn expected expression environment =
    match inferExpression(expression)(environment) with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, error = None } ->
            let actual =
                semanticType
                |> applySubstitution(substitution)
                |> formatSemanticType
            in
                if actual == expected
                then Unit
                else test.fail("expected " + expected + " but inferred " + actual)
        | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(error) } ->
            test.fail(
                "inference should succeed: " + Ashes.Trait.Show.show(error)
            )

let expectType expected expression =
    Unit
    |> emptyTypeEnvironment
    |> expectTypeIn(expected)(expression)

let recursive hasConstraint expectedTrait expectedType substitution constraints =
    match constraints with
        | [] -> false
        | TraitConstraint { traitName = traitName, typeArguments = semanticType :: [] } :: tail ->
            if traitName == expectedTrait
            then
                if formatSemanticType(applySubstitution(substitution)(semanticType)) == expectedType
                then true
                else hasConstraint(expectedTrait)(expectedType)(substitution)(tail)
            else hasConstraint(expectedTrait)(expectedType)(substitution)(tail)
        | _ :: tail -> hasConstraint(expectedTrait)(expectedType)(substitution)(tail)

let expectConstraint expectedTrait expectedType expression =
    match Unit
    |> emptyTypeEnvironment
    |> inferExpression(expression) with
        | TypeInferenceResult { semanticType = _semanticType, substitution = substitution, supply = _supply, constraints = constraints, error = None } ->
            if hasConstraint(expectedTrait)(expectedType)(substitution)(constraints)
            then Unit
            else test.fail("expected constraint " + expectedTrait + "(" + expectedType + ")")
        | _ -> test.fail("constraint expression should infer")

let expectGenericBinaryTrait expectedTrait expression =
    match Unit
    |> emptyTypeEnvironment
    |> inferExpression(expression) with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = constraints, error = None } ->
            match applySubstitution(substitution)(semanticType) with
                | SemFunction(SemVariable(leftId), SemFunction(SemVariable(rightId), SemVariable(resultId), Some(_innerRow)), Some(_outerRow)) ->
                    if leftId == rightId
                    then
                        if rightId == resultId
                        then
                            if hasConstraint(expectedTrait)("?" + Ashes.Text.fromInt(leftId))(substitution)(constraints)
                            then Unit
                            else test.fail("generic binary expression should preserve its trait constraint")
                        else test.fail("generic binary expression should use one type for both operands and its result")
                    else test.fail("generic binary expression should use one type for both operands and its result")
                | _ -> test.fail("generic binary expression should infer a curried binary function")
        | _ -> test.fail("generic binary expression should infer")

let expectPolymorphicIdentity expression =
    match Unit
    |> emptyTypeEnvironment
    |> inferExpression(expression) with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = _constraints, error = None } ->
            match applySubstitution(substitution)(semanticType) with
                | SemFunction(SemVariable(argumentId), SemVariable(resultId), Some(_row)) ->
                    if argumentId == resultId
                    then Unit
                    else test.fail("identity argument and result should share one type variable")
                | _ -> test.fail("expression should infer a polymorphic identity function")
        | _ -> test.fail("polymorphic identity expression should infer")

let expectIntIdentity expression =
    match Unit
    |> emptyTypeEnvironment
    |> inferExpression(expression) with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = _constraints, error = None } ->
            match applySubstitution(substitution)(semanticType) with
                | SemFunction(SemInt, SemInt, Some(_row)) -> Unit
                | _ -> test.fail("annotated identity should infer an open-row Int function")
        | _ -> test.fail("annotated identity expression should infer")

let identityExpression unit = ExprLambda("value")(ExprVar("value"))(None)

let genericAddExpression unit =
    ExprLambda("left")(ExprLambda("right")(ExprAdd(ExprVar("left"))(ExprVar("right")))(None))(None)

let expectCoreInference unit =
    (let identity = identityExpression(Unit)
    in
        let reference = ExprVar("identity")
        in
            let polymorphicBody =
                ExprTuple(
                    [ExprCall(reference)(ExprInt(1))(false)(callArgumentsInline), ExprCall(
                        reference,
                        ExprString("text"),
                        false,
                        callArgumentsInline
                    )]
                )
            in
                unit
                |> (given (_) -> expectPolymorphicIdentity(identity))
                |> (given (_) ->
                    callArgumentsInline
                    |> ExprCall(identity)(ExprInt(42))(false)
                    |> expectType("Int"))
                |> (given (_) ->
                    []
                    |> ExprLet("identity")(identity)(polymorphicBody)([])(None)
                    |> expectType("(Int, Str)"))
                |> (given (_) ->
                    ExprInt(2)
                    |> ExprIf(ExprBool(true))(ExprInt(1))
                    |> expectType("Int"))
                |> (given (_) ->
                    false
                    |> ExprList([ExprInt(1), ExprInt(2)])
                    |> expectType("List(Int)"))
                |> (given (_) ->
                    false
                    |> ExprList([])
                    |> ExprCons(ExprString("head"))
                    |> expectType("List(Str)"))
                |> (given (_) ->
                    []
                    |> ExprLetRecursive(
                        "loop",
                        identity,
                        ExprCall(ExprVar("loop"))(ExprInt(1))(false)(callArgumentsInline),
                        [],
                        None
                    )
                    |> expectType("Int")))

let expectQualifiedAndConstrainedInference unit =
    (let environment =
        Unit
        |> emptyTypeEnvironment
        |> addTypeBinding("Config.value")(TypeScheme(quantified = [], body = SemString, constraints = []))
    in
        unit
        |> (given (_) ->
            expectTypeIn("Str")(ExprQualifiedVar("Config")("value"))(environment))
        |> (given (_) ->
            ExprInt(2)
            |> ExprAdd(ExprInt(1))
            |> expectConstraint("Add")("Int"))
        |> (given (_) ->
            ExprString("right")
            |> ExprEqual(ExprString("left"))
            |> expectConstraint("Eq")("Str"))
        |> (given (_) ->
            Unit
            |> genericAddExpression
            |> expectGenericBinaryTrait("Add")))

let expectInferenceAnnotations unit =
    unit
    |> (given (_) ->
        Some(TypeNamed("Int"))
        |> ExprLambda("value")(ExprVar("value"))
        |> expectIntIdentity)
    |> (given (_) ->
        []
        |> ExprLet("identity")(identityExpression(Unit))(ExprVar("identity"))([])(None
        |> TypeArrow(TypeNamed("a"))(TypeNamed("a"))([])
        |> Some)
        |> expectPolymorphicIdentity)

let rejectInvalidInferenceAnnotation unit =
    (let expression = ExprLet("value")(ExprInt(1))(ExprVar("value"))([])(Some(TypeNamed("Str")))([])
    in
        match Unit
        |> emptyTypeEnvironment
        |> inferExpression(expression) with
            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(InferenceUnificationError(TypeMismatch(_left, _right))) } -> Unit
            | _ -> test.fail("let annotations should constrain inferred values"))

let rejectUnknownInferenceAnnotation unit =
    (let expression = ExprLet("value")(ExprInt(1))(ExprVar("value"))([])(Some(TypeNamed("Missing")))([])
    in
        match Unit
        |> emptyTypeEnvironment
        |> inferExpression(expression) with
            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(InferenceTypeResolutionError(UnknownTypeName("Missing"))) } -> Unit
            | _ -> test.fail("unknown annotation types should be rejected"))

let expectNominalInferenceAnnotation unit =
    (let boxedType = SemNamed(10)("Box")([SemInt])
    in
        let environment =
            Unit
            |> emptyTypeEnvironment
            |> addTypeBinding("boxed")(TypeScheme(quantified = [], body = boxedType, constraints = []))
            |> addInferenceTypeDefinition(10)("Box")(1)
        in
            expectTypeIn("Box(Int)")(ExprLet("value")(ExprVar("boxed"))(ExprVar("value"))([])([TypeNamed("Int")]
            |> TypeApplied("Box")
            |> Some)([]))(environment))

let expectResolvedLetConstraint unit =
    (let genericAdd = genericAddExpression(Unit)
    in
        let applied =
            ExprCall(
                ExprCall(genericAdd)(ExprInt(1))(false)(callArgumentsInline),
                ExprInt(2),
                false,
                callArgumentsInline
            )
        in
            []
            |> ExprLet("sum")(applied)(ExprVar("sum"))([])(None)
            |> expectConstraint("Add")("Int"))

let rejectSelfApplication unit =
    (let expression =
        ExprLambda("value")(ExprCall(ExprVar("value"))(ExprVar("value"))(false)(callArgumentsInline))(None)
    in
        match Unit
        |> emptyTypeEnvironment
        |> inferExpression(expression) with
            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(InferenceUnificationError(InfiniteType(_variableId, _type))) } -> Unit
            | _ -> test.fail("self application should fail the occurs check"))

let rejectBranchMismatch unit =
    match Unit
    |> emptyTypeEnvironment
    |> inferExpression(ExprIf(ExprBool(true))(ExprInt(1))(ExprString("wrong"))) with
        | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(InferenceUnificationError(TypeMismatch(_left, _right))) } -> Unit
        | _ -> test.fail("if branches should have one type")

let rejectUnknownValue unit =
    match Unit
    |> emptyTypeEnvironment
    |> inferExpression(ExprVar("missing")) with
        | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(UnknownValue("missing")) } -> Unit
        | _ -> test.fail("unknown value should be reported")

let runTypeInferenceTests unit =
    unit
    |> expectCoreInference
    |> expectQualifiedAndConstrainedInference
    |> expectInferenceAnnotations
    |> rejectInvalidInferenceAnnotation
    |> rejectUnknownInferenceAnnotation
    |> expectNominalInferenceAnnotation
    |> expectResolvedLetConstraint
    |> rejectSelfApplication
    |> rejectBranchMismatch
    |> rejectUnknownValue
    |> (given (_) -> Ashes.IO.print("all self-hosted annotation-aware inference tests passed"))
