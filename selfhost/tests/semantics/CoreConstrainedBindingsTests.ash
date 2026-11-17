import AshesCompiler.Frontend.Parser
import AshesCompiler.Semantics.CoreConstrainedBindings
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.StandardTraits.standardTraitEnvironment
export (
    value runCoreConstrainedBindingsTests,
)

let assert condition message =
    if condition
    then Unit
    else Ashes.IO.panic(message)

let parse source =
    match parseExpression(source) with
        | ExpressionParseResult { expression = expression, diagnostics = [] } -> expression
        | _ -> Ashes.IO.panic("constrained planning source did not parse")

let recursive hasMethod (traitName: Str) (methodName: Str) (operand: SemanticType) (methods: List((Str, SemanticType, Str, Str))) =
    match methods with
        | [] -> false
        | (candidateTrait, candidateType, candidateMethod, _binding) :: rest -> candidateTrait == traitName && candidateMethod == methodName && candidateType == operand || hasMethod(traitName)(methodName)(operand)(rest)

let checkRecursiveInference environment =
    (let value = parse("given key -> given entries -> match entries with | [] -> false | item :: tail -> if item == key then true else lookup(key)(tail)")
    in
        match 1000
        |> typeVariableSupplyFrom
        |> inferCoreConstrainedBinding("lookup")(true)(value)(None)([])(environment) with
            | CoreConstrainedInference { scheme = TypeScheme { constraints = TraitConstraint { traitName = "Eq", typeArguments = SemVariable(_id) :: [] } :: [] }, boundaryRequirements = [], error = None } -> Unit
            | result -> Ashes.IO.panic("recursive lookup lost its generalized Eq requirement"))

let checkBoundaryRequirements environment =
    (let outer = addTypeBinding("outer")(TypeScheme(quantified = [], body = SemVariable(71), constraints = []))(environment)
    in
        match 1000
        |> typeVariableSupplyFrom
        |> inferCoreConstrainedBinding("read")(false)(parse("given ignored -> outer == outer"))(None)([])(outer) with
            | CoreConstrainedInference { scheme = TypeScheme { constraints = [] }, boundaryRequirements = TraitConstraint { traitName = "Eq", typeArguments = SemVariable(71) :: [] } :: [], error = None } -> Unit
            | _ -> Ashes.IO.panic("captured requirement escaped its outer scope"))

let checkLayouts environment =
    (let requirements = [TraitConstraint(traitName = "Ord", typeArguments = [SemVariable(71)])]
    in
        match coreConstrainedLayout(requirements)(environment) with
            | CoreConstrainedLayout { parameterTypes = dictionary :: [], methods = methods, error = None } ->
                Unit
                |> (given (_) ->
                    assert(hasMethod("Ord")("compare")(SemVariable(71))(methods))("Ord method missing"))
                |> (given (_) ->
                    assert(hasMethod("Eq")("equal")(SemVariable(71))(methods))("Eq supertrait evidence missing"))
                |> (given (_) ->
                    match None
                    |> SemFunction(SemVariable(71))(SemBool)
                    |> coreConstrainedPhysicalType([dictionary]) with
                        | SemFunction(actualDictionary, SemFunction(SemVariable(71), SemBool, None), None) -> assert(actualDictionary == dictionary)("physical dictionary type changed")
                        | _ -> Ashes.IO.panic("physical prefix changed the source function type"))
            | _ -> Ashes.IO.panic("Ord dictionary layout failed"))

let checkDistinctDictionaries environment =
    match coreConstrainedLayout([TraitConstraint(traitName = "Eq", typeArguments = [SemInt]), TraitConstraint(traitName = "Eq", typeArguments = [SemString])])(environment) with
        | CoreConstrainedLayout { parameterTypes = _integer :: _text :: [], methods = methods, error = None } -> assert(hasMethod("Eq")("equal")(SemInt)(methods) && hasMethod("Eq")("equal")(SemString)(methods))("same-trait dictionaries were conflated")
        | _ -> Ashes.IO.panic("distinct Eq dictionary layouts failed")

let checkUnknownTrait environment =
    match coreConstrainedLayout([TraitConstraint(traitName = "Absent", typeArguments = [SemInt])])(environment) with
        | CoreConstrainedLayout { error = Some(_error) } -> Unit
        | _ -> Ashes.IO.panic("unknown trait silently disappeared from dictionary ABI")

let checkPhysicalSlots environment =
    Unit
    |> (given (_) ->
        match coreConstrainedLayout([TraitConstraint(traitName = "Eq", typeArguments = [SemInt]), TraitConstraint(traitName = "Eq", typeArguments = [SemInt])])(environment) with
            | CoreConstrainedLayout { parameterTypes = _first :: _second :: [], error = None } -> Unit
            | _ -> Ashes.IO.panic("coincident requirements lost a physical slot"))
    |> (given (_) ->
        let first = TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(10)])
        in
            let second = TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(9)])
            in
                let scheme = TypeScheme(quantified = [(9, "a"), (10, "b")], body = SemTuple([SemVariable(9), SemVariable(10)]), constraints = [first, second])
                in
                    match 100
                    |> typeVariableSupplyFrom
                    |> instantiatePreservingConstraintOrder(scheme) with
                        | InstantiationResult { semanticType = SemTuple(SemVariable(100) :: SemVariable(101) :: []), constraints = TraitConstraint { typeArguments = SemVariable(101) :: [] } :: TraitConstraint { typeArguments = SemVariable(100) :: [] } :: [] } -> Unit
                        | _ -> Ashes.IO.panic("instantiation reordered physical dictionary slots"))

let checkSchemeBoundary unit =
    (let substitution =
        Ashes.Collection.Map.empty
        |> Ashes.Collection.Map.setInt(9)(SemString)
        |> Ashes.Collection.Map.setInt(10)(SemInt)
    in
        let scheme = TypeScheme(quantified = [(9, "a")], body = SemFunction(SemVariable(9))(SemVariable(10))(None), constraints = [TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(9)]), TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(10)])])
        in
            match applySchemeSubstitutionMap(substitution)(scheme) with
                | TypeScheme { body = SemFunction(SemVariable(9), SemInt, None), constraints = TraitConstraint { typeArguments = SemVariable(9) :: [] } :: TraitConstraint { typeArguments = SemInt :: [] } :: [] } -> Unit
                | _ -> Ashes.IO.panic("scheme substitution crossed its quantified boundary"))

let runCoreConstrainedBindingsTests unit =
    (let environment = standardTraitEnvironment(Unit)
    in
        Unit
        |> (given (_) -> checkRecursiveInference(environment))
        |> (given (_) -> checkBoundaryRequirements(environment))
        |> (given (_) -> checkLayouts(environment))
        |> (given (_) -> checkDistinctDictionaries(environment))
        |> (given (_) -> checkUnknownTrait(environment))
        |> (given (_) -> checkPhysicalSlots(environment))
        |> (given (_) -> checkSchemeBoundary(Unit)))
