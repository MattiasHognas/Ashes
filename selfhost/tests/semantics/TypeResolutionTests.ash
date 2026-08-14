import Ashes.Test as test
import AshesCompiler.Frontend.Syntax.TypeExpr
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeResolution
let expectResolved expected sourceType context =
    match resolveTypeExpression(sourceType)(context) with
        | TypeResolutionResult { semanticType = semanticType, error = None } ->
            let actual = formatSemanticType(semanticType)
            in
                if actual == expected
                then Unit
                else test.fail("expected " + expected + " but resolved " + actual)
        | TypeResolutionResult { semanticType = _semanticType, error = Some(error) } -> test.fail("type should resolve: " + Ashes.Trait.Show.show(error))

let runTypeResolutionTests unit =
    (let empty = emptyTypeResolutionContext(Unit)
    in
        let primitiveChecked = expectResolved("Int")(TypeNamed("Int"))(empty)
        in
            let unsignedChecked = expectResolved("UInt32")(TypeNamed("u32"))(empty)
            in
                let listChecked = expectResolved("List(Str)")(TypeApplied("List")([TypeNamed("Str")]))(empty)
                in
                    let parameterContext = addTypeParameter("a")(SemParameter(0)("a"))(empty)
                    in
                        let nominalContext = addTypeDefinition(10)("Box")(1)(parameterContext)
                        in
                            let nominalChecked = expectResolved("Box(a)")(TypeApplied("Box")([TypeNamed("a")]))(nominalContext)
                            in
                                let rowContext = addTypeParameter("effects")(SemVariable(9))(parameterContext)
                                in
                                    let functionType = TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([("Clock", []), ("State", [TypeNamed("a")])])(Some("effects"))
                                    in
                                        let rowChecked = expectResolved("a -> Bool needs {Clock, State(a) | ?9}")(functionType)(rowContext)
                                        in
                                            let arityChecked =
                                                match resolveTypeExpression(TypeApplied("Box")([]))(nominalContext) with
                                                    | TypeResolutionResult { semanticType = SemNever, error = Some(TypeNameArityMismatch("Box", 1, 0)) } -> Unit
                                                    | _ -> test.fail("nominal type arity should be checked")
                                            in
                                                let unknownChecked =
                                                    match resolveTypeExpression(TypeNamed("Missing"))(empty) with
                                                        | TypeResolutionResult { semanticType = SemNever, error = Some(UnknownTypeName("Missing")) } -> Unit
                                                        | _ -> test.fail("unknown type names should be rejected")
                                                in Ashes.IO.print("all self-hosted type resolution tests passed"))
