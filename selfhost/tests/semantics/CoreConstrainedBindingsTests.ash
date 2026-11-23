import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Semantics.CoreLowering.Emit.initialState
import AshesCompiler.Semantics.CoreLowering.Emit.bindType
import AshesCompiler.Semantics.CoreLowering.LoweringState.withStatePendingTraitGoals
import AshesCompiler.Semantics.CoreLowering.LoweringState.withStateActiveTraitMethods
import AshesCompiler.Semantics.CoreLowering.Operators.dictionaryGoalCandidates
import AshesCompiler.Semantics.CoreLowering.Owners.addBinding
import AshesCompiler.Semantics.CoreLowering.Model.CoreBindingLocation
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.deferredEvaluationKey
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.withRawOperatorDeclarationSchemes
import AshesCompiler.Semantics.CoreLowering.Owners.pendingOperatorScheme
import AshesCompiler.Semantics.CoreLowering.Expressions.constrainedBindingPlan
import AshesCompiler.Semantics.CoreLowering.Program.registeredProgramState
import AshesCompiler.Semantics.CoreLowering.Owners.stripExprAt
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

// Planning uses Core's live type layouts. Task has no constructor to supply
// its symbol, and a coroutine still takes the dictionary of its source lambda.
let checkAsyncPlan unit =
    (let source = "let job : a -> Task(Str, Str) requires {Show(a)} = given value -> async(let render unit = Show.show(value) in match await Ashes.Task.sleep(1) with | Ok(_) -> render(Unit) | Error(message) -> message) in job"
    in
        match source
        |> parse
        |> stripExprAt with
            | ExprLet(name, value, _body, _parameters, annotation, requirements) ->
                match Unit
                |> initialState
                |> constrainedBindingPlan(name)(false)(value)(annotation)(requirements) with
                    | Some(CoreConstrainedInference { scheme = TypeScheme { body = SemFunction(SemVariable(parameter), SemNamed(0, "Task", SemString :: SemString :: []), _row), constraints = TraitConstraint { traitName = "Show", typeArguments = SemVariable(methodParameter) :: [] } :: [] } as scheme, error = None }) ->
                        if parameter == methodParameter && !schemeHoldsVariable(parameter)(scheme)
                        then Unit
                        else Ashes.IO.panic("async planning detached Show from the generalized value parameter")
                    | _ -> Ashes.IO.panic("async lost its constrained declaration before closure lowering")
            | _ -> Ashes.IO.panic("async constrained declaration did not parse"))

// An opaque Task uses the environment's nominal identity. Core shares a
// layout symbol across named types, so another two-field type is not a task.
let checkAwaitQueryIdentity unit =
    (let types = [SemString, SemInt]
    in
        let inferAwaited =
            given (name) ->
                let environment =
                    Unit
                    |> emptyTypeEnvironment
                    |> addInferenceTypeDefinition(77)("Task")(2)
                    |> addTypeBinding("pending")(TypeScheme(quantified = [], body = SemNamed(77)(name)(types), constraints = []))
                in
                    1000
                    |> typeVariableSupplyFrom
                    |> inferExpressionFrom(parse("await pending"))(environment)([])
        in
            match (inferAwaited("Task"), inferAwaited("Other")) with
                | (TypeInferenceResult { semanticType = SemNamed(_id, "Result", SemString :: SemInt :: []), error = None }, TypeInferenceResult { error = Some(ExpectedTaskType(SemNamed(77, "Other", _arguments))) }) -> Unit
                | _ -> Ashes.IO.panic("await lost the environment's Task identity"))

let checkPendingEvidenceBoundary unit =
    (let operand = SemVariable(9001)
    in
        let supplied = SemVariable(9002)
        in
            let unrelated = SemVariable(9003)
            in
                let state =
                    Unit
                    |> initialState
                    |> withStatePendingTraitGoals([("first", operand, [("seedDictionary", supplied)]), ("other", unrelated, [])])
                in
                    let before = pendingOperatorScheme(state)
                    in
                        match bindType(operand)(supplied)(state) with
                            | (bound, None) ->
                                let after = pendingOperatorScheme(bound)
                                in assert(schemeHoldsVariable(9001)(before) && !schemeHoldsVariable(9002)(after) && !schemeHoldsVariable(9001)(after) && schemeHoldsVariable(9003)(after))("only the obligation satisfied by the enclosing dictionary may generalize")
                            | _ -> Ashes.IO.panic("pending result could not unify with its enclosing dictionary"))

// A constrained call whose dictionary still waits on a type records the enclosing body's active
// dictionaries of that trait, so the waiting type stops holding back the enclosing scheme once it
// meets one of their operands.
let checkDictionaryGoalCandidates unit =
    (let operand = SemVariable(9101)
    in
        let supplied = SemVariable(9102)
        in
            let active =
                Unit
                |> initialState
                |> withStateActiveTraitMethods([("Eq", supplied, "equal", "eqEqual"), ("Eq", supplied, "notEqual", "eqNotEqual"), ("Show", supplied, "show", "showShow")])
            in
                match dictionaryGoalCandidates("Eq")(active) with
                    | ("eqEqual", candidate) :: [] ->
                        let state = withStatePendingTraitGoals([("dictionary", operand, [("eqEqual", candidate)])])(active)
                        in
                            match bindType(operand)(supplied)(state) with
                                | (bound, None) ->
                                    assert(schemeHoldsVariable(9101)(pendingOperatorScheme(state)) && !schemeHoldsVariable(9101)(pendingOperatorScheme(bound)))("a dictionary goal answered by the enclosing dictionary still held its type")
                                | _ -> Ashes.IO.panic("dictionary goal could not unify with its enclosing dictionary")
                    | _ -> Ashes.IO.panic("dictionary goal candidates were not one per active Eq dictionary"))

// The bindings the standalone inference recorded as evaluating nothing observable: every name the
// cases below bind except `loud`.
let deferredEvaluations unit =
    (let marker = TypeScheme(quantified = [], body = SemBool, constraints = [])
    in
        Ashes.Collection.Map.empty
        |> Ashes.Collection.Map.setStr(deferredEvaluationKey("saved"))(marker)
        |> Ashes.Collection.Map.setStr(deferredEvaluationKey("held"))(marker))

let callbackPlanState unit =
    (let pickType =
        SemFunction(SemTuple([]))(SemFunction(SemVariable(5))(SemBool)(None))(None)
    in
        let firstType = SemFunction(SemVariable(7))(SemVariable(6))(None)
        in
            Unit
            |> initialState
            |> withRawOperatorDeclarationSchemes(deferredEvaluations(Unit))
            |> addBinding("keep")(TypeScheme(quantified = [(4, "b")], body = SemFunction(SemVariable(4))(SemVariable(4))(None), constraints = []))(CoreLocal(0))
            |> addBinding("pick")(TypeScheme(quantified = [(5, "a")], body = pickType, constraints = [TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(5)])]))(CoreLocal(1))
            |> addBinding("first")(TypeScheme(quantified = [(6, "c"), (7, "d")], body = firstType, constraints = [TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(6)])]))(CoreLocal(2)))

let callbackPlan source =
    match source
    |> parse
    |> stripExprAt with
        | ExprLet(name, value, _body, _parameters, annotation, requirements) ->
            Unit
            |> callbackPlanState
            |> constrainedBindingPlan(name)(false)(value)(annotation)(requirements)
        | _ -> Ashes.IO.panic("callback binding did not parse")

// A top-level call of an unconstrained function that returns a constrained callback it was given
// keeps the callback's requirement on the binding when the binding's evaluation observes nothing;
// a binding not recorded so, or a value that is not a function, keeps none.
let checkForwardedCallbackPlan unit =
    Unit
    |> (given (_) ->
        match callbackPlan("let saved = keep(pick) in saved") with
            | Some(CoreConstrainedInference { scheme = TypeScheme { body = SemFunction(_unit, SemFunction(SemVariable(operand), SemBool, _inner), _outer), constraints = TraitConstraint { traitName = "Eq", typeArguments = SemVariable(required) :: [] } :: [] } as scheme, error = None }) -> assert(operand == required && !schemeHoldsVariable(operand)(scheme))("a kept callback's requirement was not generalized on the binding")
            | _ -> Ashes.IO.panic("a kept constrained callback had no dictionary plan"))
    |> (given (_) ->
        match callbackPlan("let loud = keep(pick) in loud") with
            | None -> Unit
            | Some(_plan) -> Ashes.IO.panic("a binding whose evaluation may be observed was evaluated per use"))
    |> (given (_) ->
        match callbackPlan("let held = keep(first(Unit)) in held") with
            | None -> Unit
            | Some(_plan) -> Ashes.IO.panic("a constrained value that is not a function was given a dictionary plan"))

// A program's `capability Ord` with a `compare` operation shadows the shipped `Ord` trait's method
// in the bindings lowering plans: `Ord.compare` in an unannotated lambda is the operation, whose
// row carries the capability, so the binding needs no `Ord` dictionary.
let checkCapabilityOperationPlan unit =
    (let source = "capability Ord(a) =\n    | compare : a -> a -> Int\n\nlet cmp = given (x) -> given (y) -> Ord.compare(x)(y)\n"
    in
        match parseProgram(source) with
            | ProgramParseResult { program = parsed, diagnostics = [] } ->
                match Unit
                |> initialState
                |> registeredProgramState(parsed.items) with
                    | Ok(state) ->
                        match constrainedBindingPlan("cmp")(false)(parse("given (x) -> given (y) -> Ord.compare(x)(y)"))(None)([])(state) with
                            | None -> Unit
                            | Some(_plan) -> Ashes.IO.panic("a capability operation call was planned as the shipped Ord trait's method")
                    | Error(_error) -> Ashes.IO.panic("the capability declaration did not register")
            | _ -> Ashes.IO.panic("the capability program did not parse"))

let runCoreConstrainedBindingsTests unit =
    (let environment = standardTraitEnvironment(Unit)
    in
        Unit
        |> (given (_) -> checkRecursiveInference(environment))
        |> checkAsyncPlan
        |> checkPendingEvidenceBoundary
        |> checkDictionaryGoalCandidates
        |> checkForwardedCallbackPlan
        |> checkCapabilityOperationPlan
        |> checkAwaitQueryIdentity
        |> (given (_) -> checkBoundaryRequirements(environment))
        |> (given (_) -> checkLayouts(environment))
        |> (given (_) -> checkDistinctDictionaries(environment))
        |> (given (_) -> checkUnknownTrait(environment))
        |> (given (_) -> checkPhysicalSlots(environment))
        |> (given (_) -> checkSchemeBoundary(Unit)))
