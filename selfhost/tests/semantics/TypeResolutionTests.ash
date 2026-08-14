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

let expectBasicTypeResolution unit =
    (let context = emptyTypeResolutionContext(Unit)
    in
        unit
        |> (given (_) -> expectResolved("Int")(TypeNamed("Int"))(context))
        |> (given (_) -> expectResolved("UInt32")(TypeNamed("u32"))(context))
        |> (given (_) ->
            expectResolved("List(Str)")(TypeApplied("List")([TypeNamed("Str")]))(context)))

let expectParameterizedTypeResolution unit =
    (let parameterContext =
        Unit
        |> emptyTypeResolutionContext
        |> addTypeParameter("a")(SemParameter(0)("a"))
    in
        let nominalContext = addTypeDefinition(10)("Box")(1)(parameterContext)
        in
            let rowContext = addTypeParameter("effects")(SemVariable(9))(parameterContext)
            in
                unit
                |> (given (_) ->
                    expectResolved("Box(a)")(TypeApplied("Box")([TypeNamed("a")]))(nominalContext))
                |> (given (_) ->
                    expectResolved("a -> Bool needs {Clock, State(a) | ?9}")(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([("Clock", []), ("State", [TypeNamed("a")])])(Some("effects")))(rowContext)))

let rejectInvalidTypeResolution unit =
    (let nominalContext =
        Unit
        |> emptyTypeResolutionContext
        |> addTypeDefinition(10)("Box")(1)
    in
        let arityChecked =
            match resolveTypeExpression(TypeApplied("Box")([]))(nominalContext) with
                | TypeResolutionResult { semanticType = SemNever, error = Some(TypeNameArityMismatch("Box", 1, 0)) } -> Unit
                | _ -> test.fail("nominal type arity should be checked")
        in
            match Unit
            |> emptyTypeResolutionContext
            |> resolveTypeExpression(TypeNamed("Missing")) with
                | TypeResolutionResult { semanticType = SemNever, error = Some(UnknownTypeName("Missing")) } -> Unit
                | _ -> test.fail("unknown type names should be rejected"))

let runTypeResolutionTests unit =
    unit
    |> expectBasicTypeResolution
    |> expectParameterizedTypeResolution
    |> rejectInvalidTypeResolution
    |> (given (_) -> Ashes.IO.print("all self-hosted type resolution tests passed"))
