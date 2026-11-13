import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Frontend.Token
type CoreFunctionState =
    | admissibilityAnswers: List((SemanticType, Bool))
    | pendingTraitGoals: List((Str, SemanticType))
    | traitGoalsPinned: Bool
    | active: Bool

type CoreProgramState =
    | provenTraitGoals: MapTree(Str, SemanticType)

type CoreLoweringState =
    | substitution: MapTree(Int, SemanticType)
    | functionState: CoreFunctionState
    | programState: CoreProgramState
    | currentSpan: Maybe(TextSpan)
    | nextTemp: Int

type CoreLoweringError =
    | CoreCallTypeMismatch(UnificationError, Unit)
    | CoreOperatorTypeMismatch(Str, SemanticType, SemanticType)

type LoweredCoreValue =
    | state: CoreLoweringState
    | temp: Int
    | semanticType: SemanticType
    | error: Maybe(CoreLoweringError)

let mismatchSiteOf _state = Unit

type Instruction =
    | LoadConstInt(Int, Int)
    | LoadConstBool(Int, Bool)
    | CmpIntEq(Int, Int, Int)

type ReservedCoreTemps =
    | state: CoreLoweringState
    | first: Int

let withNextTemp n (state: CoreLoweringState) = state with nextTemp = n

let emit _instruction state = state

let success temp semanticType state = LoweredCoreValue(state = state, temp = temp, semanticType = semanticType, error = None)

let lowerConstant _make semanticType (state: CoreLoweringState) = success(state.nextTemp)(semanticType)((state with nextTemp = state.nextTemp + 1))

let lowerString _text (state: CoreLoweringState) = success(state.nextTemp)(SemString)((state with nextTemp = state.nextTemp + 1))

let hasActiveTraitEvidence _traitName _operandType state =
    (let group = state.functionState
    in group.active)

let emitCoreTraitUnaryDispatch _traitName _methodName resultType _lower operandTemp _operandType state = success(operandTemp)(resultType)(state)

let noLower _expression state = success(0)(SemNever)(state)

let clearAdmissibilityAnswers (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with admissibilityAnswers = []))

let withSubstitution substitution (state: CoreLoweringState) = clearAdmissibilityAnswers((state with substitution = substitution))

let resolveType state semanticType =
    match state with
        | CoreLoweringState { substitution = substitution } -> applySubstitutionMap(substitution)(semanticType)

// The environment a `let` generalizes against, read through the current substitution: a variable
// an enclosing binding's type was unified with since its scheme was recorded (a captured
// parameter whose variable now stands for a constructor field's) is the environment's and must
// not be quantified, the Hindley-Milner rule stage 0 follows by pruning the environment first.
let failure state error =
    LoweredCoreValue(
        state = state,
        temp = -1,
        semanticType = SemNever,
        error = Some(error)
    )

let bindType left right state =
    match state with
        | CoreLoweringState { substitution = existing } ->
            match right
            |> resolveType(state)
            |> unify(resolveType(state)(left)) with
                | UnificationResult { substitution = added, error = None } ->
                    match added with
                        | [] -> (state, None)
                        | _ ->
                            (withSubstitution(extendSubstitutionMap(added)(existing))(state), None)
                | UnificationResult { error = Some(error) } ->
                    (state, state
                    |> mismatchSiteOf
                    |> CoreCallTypeMismatch(error)
                    |> Some)

// The type the context expects of the next expression lowered, threaded through the state:
// `lowerCore` consumes it. A let, recursive binding, lambda, if, match, handle, call, list
// literal, or cons forwards it to the parts stage 0 forwards it to; every other expression is
// lowered without it and unified with it afterwards. The result state never carries one.
let statePendingTraitGoals (state: CoreLoweringState) =
    (let group = state.functionState
    in group.pendingTraitGoals)

let withStatePendingTraitGoals value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with pendingTraitGoals = value))

let stateProvenTraitGoals (state: CoreLoweringState) =
    (let group = state.programState
    in group.provenTraitGoals)

let withStateProvenTraitGoals value (state: CoreLoweringState) =
    (let group = state.programState
    in state with programState = (group with provenTraitGoals = value))

let stateTraitGoalsPinned (state: CoreLoweringState) =
    (let group = state.functionState
    in group.traitGoalsPinned)

let withStateTraitGoalsPinned value (state: CoreLoweringState) =
    (let group = state.functionState
    in state with functionState = (group with traitGoalsPinned = value))

let traitGoalKey (traitName: Str) (methodName: Str) (state: CoreLoweringState) =
    match state.currentSpan with
        | Some(TextSpan { start = start, end = end }) -> Some(Ashes.Text.fromInt(start) + ":" + Ashes.Text.fromInt(end) + ":" + traitName + ":" + methodName)
        | None -> None

// The operand type a goal dispatches on, with an earlier pass's proof pinned when the type is
// still open. Unifying rather than substituting for the evidence alone keeps the rest of inference
// consistent with the choice.
let pinProvenTraitGoal (traitName: Str) (methodName: Str) (operandType: SemanticType) (state: CoreLoweringState) =
    match (freeTypeVariables(operandType), traitGoalKey(traitName)(methodName)(state)) with
        | ([], _key) -> (state, operandType)
        | (_variables, None) -> (state, operandType)
        | (_variables, Some(key)) ->
            match state
            |> stateProvenTraitGoals
            |> Ashes.Collection.Map.getStr(key) with
                | None -> (state, operandType)
                | Some(proven) ->
                    match bindType(operandType)(proven)(state) with
                        | (pinnedState, None) -> (pinnedState, resolveType(pinnedState)(operandType))
                        | (_failedState, Some(_error)) -> (state, operandType)

// Whether a plan's evidence is a hidden parameter of a generic body this body does not carry.
// `resolveTraitEvidence` answers with that shape for an operand type inference has not settled
// too, which at the site is indistinguishable from a real requirement's type argument.
let unprovenTraitGoalKey (traitName: Str) (methodName: Str) (operandType: SemanticType) (state: CoreLoweringState) =
    match (stateTraitGoalsPinned(state), freeTypeVariables(operandType)) with
        | (true, _variables) -> None
        | (_pinned, []) -> None
        | (_pinned, _variables) -> traitGoalKey(traitName)(methodName)(state)

// The value lowered at a deferred site: the goal joins the pass's record, and the site gets a
// constant of the method's own result type. A deferring pass exists to resolve types, and its
// instructions are discarded, so the constant is never run.
let deferTraitGoal (key: Str) (operandType: SemanticType) (resultType: SemanticType) (state: CoreLoweringState) =
    (let recorded = withStatePendingTraitGoals((key, operandType) :: statePendingTraitGoals(state))(state)
    in
        match resultType with
            | SemString -> lowerString("")(recorded)
            | _ ->
                lowerConstant(given (target) -> LoadConstInt(target)(0))(resultType)(recorded))

// `Ashes.Trait.Show.show(x)` and its unary siblings, the counterpart of the mapped binary operators:
// resolve the trait's evidence for the operand's own type, build the method's closure for that plan,
// and call it. A concrete operand resolves an instance directly; inside a generic body the operand's
// type is the type argument of an active requirement and resolves to that parameter's evidence.
let reserveCoreTemps count state =
    match state with
        | CoreLoweringState { nextTemp = first } ->
            ReservedCoreTemps(
                state = withNextTemp(first + count)(state),
                first = first
            )

let finishCoreLogicalNot lowered =
    match lowered with
        | LoweredCoreValue { state = state, temp = operand, semanticType = semanticType, error = None } ->
            match resolveType(state)(semanticType) with
                | SemBool ->
                    match reserveCoreTemps(2)(state) with
                        | ReservedCoreTemps { state = targetState, first = falseTemp } ->
                            targetState
                            |> emit(LoadConstBool(falseTemp)(false))
                            |> emit(CmpIntEq(falseTemp + 1)(operand)(falseTemp))
                            |> success(falseTemp + 1)(SemBool)
                | other ->
                    other
                    |> CoreOperatorTypeMismatch("not")(other)
                    |> failure(state)
        | LoweredCoreValue { state = state, error = Some(error) } -> failure(state)(error)

let recursive resolvedTraitGoals (pending: List((Str, SemanticType))) (state: CoreLoweringState) (resolved: List((Str, SemanticType))) =
    match pending with
        | [] -> resolved
        | (key, goalType) :: rest ->
            let goalResolvedType = resolveType(state)(goalType)
            in
                match freeTypeVariables(goalResolvedType) with
                    | [] -> resolvedTraitGoals(rest)(state)((key, goalResolvedType) :: resolved)
                    | _variables -> resolvedTraitGoals(rest)(state)(resolved)

let recursive traitGoalMap (entries: List((Str, SemanticType))) (proven: MapTree(Str, SemanticType)) =
    match entries with
        | [] -> proven
        | (key, goalType) :: rest ->
            proven
            |> Ashes.Collection.Map.setStr(key)(goalType)
            |> traitGoalMap(rest)

// Stage 0's discovery pass, run only for a program that needs one: a pass that met a trait goal
// its substitution could not yet answer lowers the program a second time with what that pass went
// on to prove, so each such site chooses the evidence the rest of the program settled on. The
// first pass's own state is a value, so the second starts from the same seed rather than from
// anything the first left behind. Its result is never the program's: a deferred site holds a
// constant standing in for a dispatch, so every pass that deferred one is followed by a pass that
// either dispatches it or reports it.
let seed n = CoreLoweringState(substitution = Ashes.Collection.Map.setInt(n)(SemVariable(n + 1))(Ashes.Collection.Map.empty), functionState = CoreFunctionState(admissibilityAnswers = [(SemVariable(n), false)], pendingTraitGoals = [], traitGoalsPinned = false, active = false), programState = CoreProgramState(provenTraitGoals = Ashes.Collection.Map.empty), currentSpan = Some(TextSpan(start = n, end = n + 1)), nextTemp = 8)

let direct n =
    (let key = Ashes.Text.fromInt(n) + ":Not:not"
    in
        let goals =
            resolvedTraitGoals([(key, SemBool)])(seed(n))([])
        in Ashes.Collection.List.length(goals) == 1)

let recursive rounds n =
    if n == 0
    then Ashes.IO.print("direct passed")
    else
        if direct(n)
        then rounds(n - 1)
        else Ashes.IO.panic("bad result")

match Ashes.IO.args with
    | "1" :: [] -> rounds(1)
    | "1000" :: [] -> rounds(1000)
    | "10000" :: [] -> rounds(10000)
    | _ -> Ashes.IO.panic("count")
