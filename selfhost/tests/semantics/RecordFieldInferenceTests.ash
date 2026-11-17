import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
let scheme body = TypeScheme(quantified = [], body = body, constraints = [])

let box argument = SemNamed(100)("Box")([argument])

let boxScheme =
    TypeScheme(quantified = [(10, "a")], body = SemFunction(SemVariable(10))(SemFunction(SemList(SemVariable(10)))(box(SemVariable(10)))(None))(None), constraints = [])

let baseEnvironment unit =
    unit
    |> emptyTypeEnvironment
    |> addConstructorBinding("Box")(boxScheme)(["value", "history"])

let infer expression environment =
    10000
    |> typeVariableSupplyFrom
    |> inferExpressionFrom(expression)(environment)([])

let expectType name expected result =
    match result with
        | TypeInferenceResult { semanticType = value, substitution = substitution, error = None } ->
            if formatSemanticType(applySubstitution(substitution)(value)) == expected
            then Unit
            else
                test.fail(name + ": unexpected type " + formatSemanticType(applySubstitution(substitution)(value)))
        | TypeInferenceResult { error = Some(error) } -> test.fail(name + ": " + Ashes.Trait.Show.show(error))

let expectFailure name result =
    match result with
        | TypeInferenceResult { error = Some(_error) } -> Unit
        | _ -> test.fail(name + ": unexpectedly accepted")

let receiver typeValue environment =
    addTypeBinding("item")(scheme(typeValue))(environment)

let valueField = ExprQualifiedVar("item")("value")

let checkKnownAndGeneric unit =
    unit
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> receiver(box(SemInt))
        |> infer(valueField)
        |> expectType("concrete field")("Int"))
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> receiver(box(SemString))
        |> infer(ExprQualifiedVar("item")("history"))
        |> expectType("generic nested field")("List(Str)"))
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> addTypeBinding("item")(TypeScheme(quantified = [(20, "b")], body = box(SemVariable(20)), constraints = []))
        |> infer(ExprTuple([ExprEqual(valueField)(ExprInt(1)), ExprEqual(valueField)(ExprString("text"))]))
        |> expectType("independent receiver instances")("(Bool, Bool)"))
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> receiver(box(SemVariable(20)))
        |> infer(ExprTuple([ExprEqual(valueField)(ExprInt(1)), ExprEqual(valueField)(ExprString("text"))]))
        |> expectFailure("captured receiver remains monomorphic"))
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> receiver(box(SemInt))
        |> infer(ExprQualifiedVar("item")("missing"))
        |> expectFailure("missing field"))
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> receiver(SemInt)
        |> infer(valueField)
        |> expectFailure("non-record receiver"))
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> receiver(SemVariable(40))
        |> (given (environment) ->
            10000
            |> typeVariableSupplyFrom
            |> inferExpressionFrom(valueField)(environment)([(40, box(SemString))]))
        |> expectType("substituted receiver")("Str"))

let checkUniqueAndAmbiguous unit =
    unit
    |> (given (_) ->
        match Unit
        |> baseEnvironment
        |> receiver(SemVariable(40))
        |> infer(ExprEqual(valueField)(ExprInt(7))) with
            | TypeInferenceResult { substitution = substitution, error = None } ->
                if formatSemanticType(applySubstitution(substitution)(SemVariable(40))) == "Box(Int)"
                then Unit
                else test.fail("unique field must constrain receiver and field together")
            | _ -> test.fail("unique field should infer"))
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> addConstructorBinding("Other")(None
        |> SemFunction(SemInt)(SemNamed(101)("Other")([]))
        |> scheme)(["value"])
        |> receiver(SemVariable(40))
        |> infer(valueField)
        |> expectFailure("ambiguous field"))
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> addConstructorBinding("Other")(None
        |> SemFunction(SemInt)(SemNamed(101)("Other")([]))
        |> scheme)(["value"])
        |> receiver(box(SemString))
        |> infer(valueField)
        |> expectType("known receiver resolves shared field")("Str"))
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> addConstructorBinding("Box")(None
        |> SemFunction(SemInt)(SemNamed(102)("Box")([]))
        |> scheme)(["other"])
        |> receiver(SemVariable(40))
        |> infer(valueField)
        |> expectFailure("shadowed constructor is not a candidate"))

let checkPrecedence unit =
    unit
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> receiver(box(SemInt))
        |> addTypeBinding("item.value")(scheme(SemString))
        |> infer(valueField)
        |> expectType("qualified value precedes record fallback")("Str"))
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> receiver(box(SemInt))
        |> receiver(box(SemString))
        |> infer(valueField)
        |> expectType("nearest lexical receiver")("Str"))
    |> (given (_) ->
        Unit
        |> baseEnvironment
        |> receiver(box(SemInt))
        |> addTypeBinding("item.value")(scheme(SemString))
        |> addCapabilityBinding("item")([]
        |> SemCapability("item")
        |> scheme)([CapabilityOperationInferenceDefinition(name = "value", scheme = scheme(SemBool), hasExplicitSignature = true)])
        |> infer(valueField)
        |> expectType("capability precedes qualified value")("Bool"))
    |> (given (_) ->
        match Unit
        |> emptyTypeEnvironment
        |> infer(ExprQualifiedVar("Missing")("value")) with
            | TypeInferenceResult { error = Some(UnknownValue("Missing.value")) } -> Unit
            | _ -> test.fail("unknown module keeps qualified lookup diagnostic"))

let checkConstraints unit =
    (let environment =
        unit
        |> baseEnvironment
        |> addConstructorBinding("Box")((boxScheme with constraints = [TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(10)])]))(["value", "history"])
        |> addTypeBinding("item")(TypeScheme(quantified = [], body = box(SemInt), constraints = [TraitConstraint(traitName = "Ord", typeArguments = [SemInt])]))
    in
        match infer(valueField)(environment) with
            | TypeInferenceResult { substitution = substitution, constraints = constraints, error = None } ->
                match applyInferenceConstraints(substitution)(constraints) with
                    | TraitConstraint { traitName = "Ord", typeArguments = SemInt :: [] } :: TraitConstraint { traitName = "Eq", typeArguments = SemInt :: [] } :: [] -> Unit
                    | _ -> test.fail("receiver and constructor constraints must survive field selection")
            | _ -> test.fail("constrained field inference should succeed"))

let runRecordFieldInferenceTests unit =
    unit
    |> checkKnownAndGeneric
    |> checkUniqueAndAmbiguous
    |> checkPrecedence
    |> checkConstraints
