import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
let expectUnified expected left right =
    match unify(left)(right) with
        | UnificationResult { substitution = substitution, error = None } ->
            let actual =
                left
                |> applySubstitution(substitution)
                |> formatSemanticType
            in
                if actual == expected
                then Unit
                else test.fail("unexpected unified type: " + actual)
        | UnificationResult { substitution = _substitution, error = Some(error) } ->
            test.fail(
                "types should unify: " + Ashes.Trait.Show.show(error)
            )

let expectInfinite variableId left right =
    match unify(left)(right) with
        | UnificationResult { substitution = _substitution, error = Some(InfiniteType(actualId, _semanticType)) } ->
            test.assertEqual(
                variableId,
                actualId
            )
        | _ -> test.fail("expected occurs-check failure")

let expectMismatch left right =
    match unify(left)(right) with
        | UnificationResult { substitution = _substitution, error = Some(TypeMismatch(_actualLeft, _actualRight)) } -> Unit
        | _ -> test.fail("expected type mismatch")

let expectArity left right =
    match unify(left)(right) with
        | UnificationResult { substitution = _substitution, error = Some(TypeArityMismatch(_leftCount, _rightCount)) } -> Unit
        | _ -> test.fail("expected type arity mismatch")

let expectUnifies left right =
    match unify(left)(right) with
        | UnificationResult { substitution = _substitution, error = None } -> Unit
        | UnificationResult { substitution = _substitution, error = Some(error) } ->
            test.fail(
                "rows should unify: " + Ashes.Trait.Show.show(error)
            )

let expectOpenRowTail left right =
    match unify(left)(right) with
        | UnificationResult { substitution = substitution, error = None } ->
            if formatSemanticType(applySubstitution(substitution)(SemVariable(9))) == "{Log}"
            then Unit
            else test.fail("open row tail should contain the unmatched capability")
        | _ -> test.fail("open row should unify with a larger closed row")

let clockCapability = SemCapability("Clock")([])

let stateIntCapability = SemCapability("State")([SemInt])

let variableUnifies unit = expectUnified("Int")(SemVariable(0))(SemInt)

let listUnifies unit = expectUnified("List(Str)")(SemList(SemVariable(0)))(SemList(SemString))

let functionUnifies unit =
    Some(SemRow([SemCapability("State")([SemInt])])(None))
    |> SemFunction(SemInt)(SemList(SemInt))
    |> expectUnified("Int -> List(Int) needs {State(Int)}")(SemFunction(SemVariable(0))(SemList(SemVariable(0)))(None
    |> SemRow([SemCapability("State")([SemVariable(0)])])
    |> Some))

let namedTypeUnifies unit =
    [SemInt, SemVariable(1)]
    |> SemNamed(7)("Result")
    |> expectUnified("Result(Int, Str)")(SemNamed(7)("Result")([SemVariable(0), SemString]))

let infiniteTypeFails unit = expectInfinite(0)(SemVariable(0))(SemList(SemVariable(0)))

let primitiveMismatchFails unit = expectMismatch(SemBool)(SemString)

let nominalIdentityMismatchFails unit =
    []
    |> SemNamed(8)("Result")
    |> expectMismatch(SemNamed(7)("Result")([]))

let tupleArityMismatchFails unit = expectArity(SemTuple([SemInt]))(SemTuple([SemInt, SemString]))

let capabilityRowOrderIsIgnored unit =
    None
    |> SemRow([stateIntCapability, clockCapability])
    |> expectUnifies(SemRow([clockCapability, stateIntCapability])(None))

let parameterizedCapabilityUnifies unit =
    None
    |> SemRow([SemCapability("State")([SemString])])
    |> expectUnifies(SemRow([SemCapability("State")([SemVariable(8)])])(None))

let openRowCapturesExtraCapability unit =
    None
    |> SemRow([clockCapability, SemCapability("Log")([])])
    |> expectOpenRowTail(SemRow([clockCapability])(Some(SemVariable(9))))

let closedRowRejectsExtraCapability unit =
    None
    |> SemRow([clockCapability, stateIntCapability])
    |> expectMismatch(SemRow([clockCapability])(None))

let reportSuccess unit = Ashes.IO.print("all self-hosted row-aware unification tests passed")

let runUnificationTests unit =
    unit
    |> variableUnifies
    |> listUnifies
    |> functionUnifies
    |> namedTypeUnifies
    |> infiniteTypeFails
    |> primitiveMismatchFails
    |> nominalIdentityMismatchFails
    |> tupleArityMismatchFails
    |> capabilityRowOrderIsIgnored
    |> parameterizedCapabilityUnifies
    |> openRowCapturesExtraCapability
    |> closedRowRejectsExtraCapability
    |> reportSuccess
