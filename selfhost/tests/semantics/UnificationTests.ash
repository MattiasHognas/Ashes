import Ashes.Test as test
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
let expectUnified expected left right =
    match unify(left)(right) with
        | UnificationResult { substitution = substitution, error = None } ->
            let actual = formatSemanticType(applySubstitution(substitution)(left))
            in
                if actual == expected
                then Unit
                else test.fail("unexpected unified type: " + actual)
        | UnificationResult { substitution = _substitution, error = Some(error) } -> test.fail("types should unify: " + Ashes.Trait.Show.show(error))

let expectInfinite variableId left right =
    match unify(left)(right) with
        | UnificationResult { substitution = _substitution, error = Some(InfiniteType(actualId, _semanticType)) } -> test.assertEqual(variableId)(actualId)
        | _ -> test.fail("expected occurs-check failure")

let expectMismatch left right =
    match unify(left)(right) with
        | UnificationResult { substitution = _substitution, error = Some(TypeMismatch(_actualLeft, _actualRight)) } -> Unit
        | _ -> test.fail("expected type mismatch")

let expectArity left right =
    match unify(left)(right) with
        | UnificationResult { substitution = _substitution, error = Some(TypeArityMismatch(_leftCount, _rightCount)) } -> Unit
        | _ -> test.fail("expected type arity mismatch")

let runUnificationTests unit =
    (let variableChecked = expectUnified("Int")(SemVariable(0))(SemInt)
    in
        let listChecked = expectUnified("List(Str)")(SemList(SemVariable(0)))(SemList(SemString))
        in
            let functionChecked = expectUnified("Int -> List(Int) needs {State(Int)}")(SemFunction(SemVariable(0))(SemList(SemVariable(0)))(Some(SemRow([SemCapability("State")([SemVariable(0)])])(None))))(SemFunction(SemInt)(SemList(SemInt))(Some(SemRow([SemCapability("State")([SemInt])])(None))))
            in
                let namedChecked = expectUnified("Result(Int, Str)")(SemNamed(7)("Result")([SemVariable(0), SemString]))(SemNamed(7)("Result")([SemInt, SemVariable(1)]))
                in
                    let occursChecked = expectInfinite(0)(SemVariable(0))(SemList(SemVariable(0)))
                    in
                        let mismatchChecked = expectMismatch(SemBool)(SemString)
                        in
                            let namedMismatchChecked = expectMismatch(SemNamed(7)("Result")([]))(SemNamed(8)("Result")([]))
                            in
                                let arityChecked = expectArity(SemTuple([SemInt]))(SemTuple([SemInt, SemString]))
                                in Ashes.IO.print("all self-hosted unification tests passed"))
