import Ashes.Test as test
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.TypeExpr
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
let expectTypeIn expected expression environment =
    match inferExpression(expression)(environment) with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, error = None } ->
            let actual = formatSemanticType(applySubstitution(substitution)(semanticType))
            in
                if actual == expected
                then Unit
                else test.fail("expected " + expected + " but inferred " + actual)
        | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(error) } -> test.fail("inference should succeed: " + Ashes.Trait.Show.show(error))

let expectType expected expression = expectTypeIn(expected)(expression)(emptyTypeEnvironment(Unit))

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
    match inferExpression(expression)(emptyTypeEnvironment(Unit)) with
        | TypeInferenceResult { semanticType = _semanticType, substitution = substitution, supply = _supply, constraints = constraints, error = None } ->
            if hasConstraint(expectedTrait)(expectedType)(substitution)(constraints)
            then Unit
            else test.fail("expected constraint " + expectedTrait + "(" + expectedType + ")")
        | _ -> test.fail("constraint expression should infer")

let expectGenericBinaryTrait expectedTrait expression =
    match inferExpression(expression)(emptyTypeEnvironment(Unit)) with
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
    match inferExpression(expression)(emptyTypeEnvironment(Unit)) with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = _constraints, error = None } ->
            match applySubstitution(substitution)(semanticType) with
                | SemFunction(SemVariable(argumentId), SemVariable(resultId), Some(_row)) ->
                    if argumentId == resultId
                    then Unit
                    else test.fail("identity argument and result should share one type variable")
                | _ -> test.fail("expression should infer a polymorphic identity function")
        | _ -> test.fail("polymorphic identity expression should infer")

let expectIntIdentity expression =
    match inferExpression(expression)(emptyTypeEnvironment(Unit)) with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = _constraints, error = None } ->
            match applySubstitution(substitution)(semanticType) with
                | SemFunction(SemInt, SemInt, Some(_row)) -> Unit
                | _ -> test.fail("annotated identity should infer an open-row Int function")
        | _ -> test.fail("annotated identity expression should infer")

let runTypeInferenceTests unit =
    (let identity = ExprLambda("value")(ExprVar("value"))(None)
    in
        let identityChecked = expectPolymorphicIdentity(identity)
        in
            let applicationChecked = expectType("Int")(ExprCall(identity)(ExprInt(42))(false))
            in
                let identityReference = ExprVar("identity")
                in
                    let polymorphicBody = ExprTuple([ExprCall(identityReference)(ExprInt(1))(false), ExprCall(identityReference)(ExprString("text"))(false)])
                    in
                        let polymorphismChecked = expectType("(Int, Str)")(ExprLet("identity")(identity)(polymorphicBody)([])(None)([]))
                        in
                            let conditionalChecked = expectType("Int")(ExprIf(ExprBool(true))(ExprInt(1))(ExprInt(2)))
                            in
                                let listChecked = expectType("List(Int)")(ExprList([ExprInt(1), ExprInt(2)]))
                                in
                                    let consChecked = expectType("List(Str)")(ExprCons(ExprString("head"))(ExprList([])))
                                    in
                                        let recursiveChecked = expectType("Int")(ExprLetRecursive("loop")(identity)(ExprCall(ExprVar("loop"))(ExprInt(1))(false))([])(None)([]))
                                        in
                                            let qualifiedEnvironment = addTypeBinding("Config.value")(TypeScheme(quantified = [], body = SemString, constraints = []))(emptyTypeEnvironment(Unit))
                                            in
                                                let qualifiedChecked = expectTypeIn("Str")(ExprQualifiedVar("Config")("value"))(qualifiedEnvironment)
                                                in
                                                    let additionChecked = expectConstraint("Add")("Int")(ExprAdd(ExprInt(1))(ExprInt(2)))
                                                    in
                                                        let equalityChecked = expectConstraint("Eq")("Str")(ExprEqual(ExprString("left"))(ExprString("right")))
                                                        in
                                                            let genericAdd = ExprLambda("left")(ExprLambda("right")(ExprAdd(ExprVar("left"))(ExprVar("right")))(None))(None)
                                                            in
                                                                let genericAddChecked = expectGenericBinaryTrait("Add")(genericAdd)
                                                                in
                                                                    let annotatedIdentity = ExprLambda("value")(ExprVar("value"))(Some(TypeNamed("Int")))
                                                                    in
                                                                        let lambdaAnnotationChecked = expectIntIdentity(annotatedIdentity)
                                                                        in
                                                                            let polymorphicAnnotation = TypeArrow(TypeNamed("a"))(TypeNamed("a"))([])(None)
                                                                            in
                                                                                let polymorphicAnnotationChecked = expectPolymorphicIdentity(ExprLet("identity")(identity)(ExprVar("identity"))([])(Some(polymorphicAnnotation))([]))
                                                                                in
                                                                                    let invalidAnnotation = ExprLet("value")(ExprInt(1))(ExprVar("value"))([])(Some(TypeNamed("Str")))([])
                                                                                    in
                                                                                        let invalidAnnotationChecked =
                                                                                            match inferExpression(invalidAnnotation)(emptyTypeEnvironment(Unit)) with
                                                                                                | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(InferenceUnificationError(TypeMismatch(_left, _right))) } -> Unit
                                                                                                | _ -> test.fail("let annotations should constrain inferred values")
                                                                                        in
                                                                                            let unknownAnnotation = ExprLet("value")(ExprInt(1))(ExprVar("value"))([])(Some(TypeNamed("Missing")))([])
                                                                                            in
                                                                                                let unknownAnnotationChecked =
                                                                                                    match inferExpression(unknownAnnotation)(emptyTypeEnvironment(Unit)) with
                                                                                                        | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(InferenceTypeResolutionError(UnknownTypeName("Missing"))) } -> Unit
                                                                                                        | _ -> test.fail("unknown annotation types should be rejected")
                                                                                                in
                                                                                                    let boxedType = SemNamed(10)("Box")([SemInt])
                                                                                                    in
                                                                                                        let boxedEnvironment = addInferenceTypeDefinition(10)("Box")(1)(addTypeBinding("boxed")(TypeScheme(quantified = [], body = boxedType, constraints = []))(emptyTypeEnvironment(Unit)))
                                                                                                        in
                                                                                                            let nominalAnnotation = TypeApplied("Box")([TypeNamed("Int")])
                                                                                                            in
                                                                                                                let nominalAnnotationChecked = expectTypeIn("Box(Int)")(ExprLet("value")(ExprVar("boxed"))(ExprVar("value"))([])(Some(nominalAnnotation))([]))(boxedEnvironment)
                                                                                                                in
                                                                                                                    let appliedGenericAdd = ExprCall(ExprCall(genericAdd)(ExprInt(1))(false))(ExprInt(2))(false)
                                                                                                                    in
                                                                                                                        let resolvedLetConstraintChecked = expectConstraint("Add")("Int")(ExprLet("sum")(appliedGenericAdd)(ExprVar("sum"))([])(None)([]))
                                                                                                                        in
                                                                                                                            let selfApplication = ExprLambda("value")(ExprCall(ExprVar("value"))(ExprVar("value"))(false))(None)
                                                                                                                            in
                                                                                                                                let occursChecked =
                                                                                                                                    match inferExpression(selfApplication)(emptyTypeEnvironment(Unit)) with
                                                                                                                                        | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(InferenceUnificationError(InfiniteType(_variableId, _type))) } -> Unit
                                                                                                                                        | _ -> test.fail("self application should fail the occurs check")
                                                                                                                                in
                                                                                                                                    let branchMismatchChecked =
                                                                                                                                        match inferExpression(ExprIf(ExprBool(true))(ExprInt(1))(ExprString("wrong")))(emptyTypeEnvironment(Unit)) with
                                                                                                                                            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(InferenceUnificationError(TypeMismatch(_left, _right))) } -> Unit
                                                                                                                                            | _ -> test.fail("if branches should have one type")
                                                                                                                                    in
                                                                                                                                        let unknownChecked =
                                                                                                                                            match inferExpression(ExprVar("missing"))(emptyTypeEnvironment(Unit)) with
                                                                                                                                                | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, error = Some(UnknownValue("missing")) } -> Unit
                                                                                                                                                | _ -> test.fail("unknown value should be reported")
                                                                                                                                        in Ashes.IO.print("all self-hosted annotation-aware inference tests passed"))
