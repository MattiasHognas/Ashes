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
    (let variableChecked = expectUnified("Int")(TypeVariable(0))(TypeInt)
    in
        let listChecked = expectUnified("List(Str)")(TypeList(TypeVariable(0)))(TypeList(TypeString))
        in
            let functionChecked = expectUnified("Int -> List(Int) needs {State(Int)}")(TypeFunction(TypeVariable(0))(TypeList(TypeVariable(0)))(Some(TypeRow([TypeCapability("State")([TypeVariable(0)])])(None))))(TypeFunction(TypeInt)(TypeList(TypeInt))(Some(TypeRow([TypeCapability("State")([TypeInt])])(None))))
            in
                let namedChecked = expectUnified("Result(Int, Str)")(TypeNamed(7)("Result")([TypeVariable(0), TypeString]))(TypeNamed(7)("Result")([TypeInt, TypeVariable(1)]))
                in
                    let occursChecked = expectInfinite(0)(TypeVariable(0))(TypeList(TypeVariable(0)))
                    in
                        let mismatchChecked = expectMismatch(TypeBool)(TypeString)
                        in
                            let namedMismatchChecked = expectMismatch(TypeNamed(7)("Result")([]))(TypeNamed(8)("Result")([]))
                            in
                                let arityChecked = expectArity(TypeTuple([TypeInt]))(TypeTuple([TypeInt, TypeString]))
                                in Ashes.IO.print("all self-hosted unification tests passed"))
