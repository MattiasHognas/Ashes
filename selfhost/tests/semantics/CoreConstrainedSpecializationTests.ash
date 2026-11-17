import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.CoreConstrainedSpecialization
export (
    value runCoreConstrainedSpecializationTests,
)

let assert condition message =
    if condition
    then Unit
    else Ashes.IO.panic(message)

let runCoreConstrainedSpecializationTests unit =
    (let scheme =
        TypeScheme(quantified = [(17, "a")], body = SemFunction(SemVariable(17))(SemFunction(SemList(SemVariable(17)))(SemBool)(None))(None), constraints = [TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(17)])])
    in
        let source =
            ExprLambda("key")(ExprLambda("entries")(ExprBool(false))(None))(None)
        in
            let registered =
                Unit
                |> emptyCoreConcreteRegistry
                |> registerCoreConcreteCandidate("lookup")(source)(scheme)(2)(7)(true)
            in
                let stored = recordCoreConcreteCopy(0)([SemInt, SemList(SemInt)])(Some("copy_int"))(registered)
                in
                    Unit
                    |> (given (_) ->
                        assert(pinConcreteConstrainedFunction(scheme)([SemInt, SemList(SemInt)]) == Some(SemFunction(SemInt)(SemFunction(SemList(SemInt))(SemBool)(None))(None)))("concrete source type was not pinned"))
                    |> (given (_) -> assert(pinConcreteConstrainedFunction(scheme)([SemString, SemList(SemInt)]) == None)("inconsistent repeated type variable was accepted"))
                    |> (given (_) -> assert(pinConcreteConstrainedFunction(scheme)([SemVariable(9), SemList(SemVariable(9))]) == None)("open call was specialized"))
                    |> (given (_) -> assert(pinConcreteConstrainedFunction(scheme)([SemFunction(SemInt)(SemInt)(None), SemList(SemInt)]) == None)("higher-order call was specialized"))
                    |> (given (_) ->
                        assert(concreteConstrainedArguments(1)(SemFunction(SemInt)(SemVariable(99))(None)) == None)("independent open result entered parameter-only cache"))
                    |> (given (_) ->
                        assert(concreteConstrainedArguments(2)(SemFunction(SemInt)(SemBool)(None)) == None)("short arrow was treated as fully saturated"))
                    |> (given (_) -> assert(lookupCoreConcreteCopy(0)([SemInt, SemList(SemInt)])(stored) == Some(Some("copy_int")))("matching source copy missed"))
                    |> (given (_) -> assert(lookupCoreConcreteCopy(0)([SemString, SemList(SemString)])(stored) == None)("different parameter types shared a copy"))
                    |> (given (_) -> assert(lookupCoreConcreteCopy(1)([SemInt, SemList(SemInt)])(stored) == None)("different binding identities shared a copy"))
                    |> (given (_) ->
                        let named = recordCoreConcreteCopy(0)([SemNamed(41)("Same")([])])(Some("first_named"))(stored)
                        in assert(lookupCoreConcreteCopy(0)([SemNamed(42)("Same")([])])(named) == None)("display names replaced nominal identity"))
                    |> (given (_) ->
                        let rejected = recordCoreConcreteCopy(0)([SemBool, SemList(SemBool)])(None)(stored)
                        in assert(lookupCoreConcreteCopy(0)([SemBool, SemList(SemBool)])(rejected) == Some(None))("rejection was confused with a missing copy"))
                    |> (given (_) ->
                        match stored
                        |> registerCoreConcreteCandidate("lookup")(source)(scheme)(2)(8)(true)
                        |> lookupCoreConcreteCandidate("lookup") with
                            | Some(CoreConcreteCandidate { identity = 1, arity = 2, bindingSlot = 8, isRecursive = true }) -> Unit
                            | _ -> Ashes.IO.panic("new declaration reused old source identity")))
