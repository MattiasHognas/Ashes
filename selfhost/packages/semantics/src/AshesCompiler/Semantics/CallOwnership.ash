// The ownership rules of one general call, stage 0's `LowerAppliedClosureCall` and
// `LowerCallRestoreArena` decisions over a known callee's parameters, body, and result type: which
// parameter position the callee borrows, whether its result may keep an argument, and what a heap
// result needs to cross the call window's closing reset.
//
// Invariants:
// - Every rule is pure over already-resolved types and the callee's syntax; emission stays in
//   `CoreLowering`.
// - A callee that is not a let-bound function called by name has no facts: it borrows nothing,
//   its result reaches nothing, and its result ownership is never statically known.

import Ashes.Collection.List.length
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Semantics.HeapLayoutClassification.canArenaResetLayout
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.ResultReachSummaries.ResultReachState
import AshesCompiler.Semantics.Types
export (
    type CallCopyOut(..),
    type CoreCalleeFacts(..),
    type CallParameterReach(..),
    type PreparedCallReach(..),
    value prepareCallReach,
    value preparedArgumentReaches,
    value preparedArgumentReachesWhole,
    value preparedArgumentCannotKeepWhole,
    value preparedCallReachFlags,
    value callCopyOutInstruction,
    value listHeadCopyOf,
    value parameterAtIndex,
    value calleeParameterBorrows,
    value calleeResultReachesArgument,
    value calleeResultReachesArgumentWhole,
    value reachCountOf,
    value calleeResultCannotKeepArgumentWhole,
    value innermostStageLabel,
    value compiledResultRuntimeManaged,
    value calleeSaturatedAndEligible,
)

// What a call's heap result needs to cross the closing reset, stage 0's `GetCallCopyOutKind`: a
// string, `Bytes`, or same-arity scalar-field ADT copies its cell, a list copies its spine with
// the head copy its elements need.
type CallCopyOut =
    | ShallowCallCopyOut(Int)
    | ListCallCopyOut(ListHeadCopyKind)

// The RC-normalizing copy of a call result past the reset into `copyTemp`.
let callCopyOutInstruction (copyOut: CallCopyOut) (copyTemp: Int) (resultTemp: Int) =
    match copyOut with
        | ShallowCallCopyOut(staticSizeBytes) -> CopyOutArena(copyTemp)(resultTemp)(staticSizeBytes)(true)(RcNormalization)(None)
        | ListCallCopyOut(headCopy) -> CopyOutList(copyTemp)(resultTemp)(headCopy)(true)(RcNormalization)

// The head copy a list result's resolved element type needs beyond the scope copy-out's inline
// scalar heads: string elements copy each head string, scalar-list elements copy each inner
// spine; any other element leaves the list without a call copy-out.
let listHeadCopyOf (element: SemanticType) =
    match element with
        | SemString -> Some(StringListHead)
        | SemList(inner) ->
            if canArenaResetLayout(inner)
            then Some(InnerListHead)
            else None
        | _ -> None

// Frozen query answers for one declared argument. Counts retain duplicate-entry sums; they are
// not sets, and a component path does not count as a whole-value reach.
type CallParameterReach =
    | anyCount: Int
    | wholeCount: Int
    | exposedWholeCount: Int
    deriving {Eq, Show}

type PreparedCallReach =
    | byArgument: MapTree(Int, CallParameterReach)
    | parameterCount: Int
    | inputsEnumerated: Bool

let recursive countParameterReach (parameter: Str) (prefix: Str) (exposed: Str) (entries: List(ParameterReachEntry)) (anyCount: Int) (wholeCount: Int) (exposedWholeCount: Int) =
    match entries with
        | [] -> CallParameterReach(anyCount = anyCount, wholeCount = wholeCount, exposedWholeCount = exposedWholeCount)
        | ParameterReachEntry { parameterName = name, reachCount = count } :: rest ->
            countParameterReach(parameter)(prefix)(exposed)(rest)(anyCount + (if name == parameter || Ashes.Text.startsWith(name)(prefix)
            then count
            else 0))(wholeCount + (if name == parameter
            then count
            else 0))(exposedWholeCount + (if name == exposed
            then count
            else 0))

let recursive prepareCallReachArguments (parameters: List(Str)) (entries: List(ParameterReachEntry)) (index: Int) (prepared: PreparedCallReach) =
    match parameters with
        | [] -> prepared with parameterCount = index
        | parameter :: rest ->
            let answer = countParameterReach(parameter)(parameter + "/")("~" + parameter)(entries)(0)(0)(0)
            in prepareCallReachArguments(rest)(entries)(index + 1)((prepared with byArgument = Ashes.Collection.Map.setInt(index)(answer)(prepared.byArgument)))

let recursive reachCausesContain (causes: List(ResultReachCause)) (target: ResultReachCause) =
    match causes with
        | [] -> false
        | cause :: rest -> cause == target || reachCausesContain(rest)(target)

// Prepare only completed summaries, once at their registration boundary. The evolving reach
// fixpoint retains its original representation and never carries stale query answers.
let prepareCallReach (parameters: List(Str)) (reach: ResultReachState) = prepareCallReachArguments(parameters)(reach.counts)(0)(PreparedCallReach(byArgument = Ashes.Collection.Map.empty, parameterCount = 0, inputsEnumerated = !reachCausesContain(reach.causes)(UnenumeratedInputs)))

let preparedArgumentReaches (index: Int) (prepared: PreparedCallReach) =
    match Ashes.Collection.Map.getInt(index)(prepared.byArgument) with
        | Some(CallParameterReach { anyCount = count }) -> count > 0
        | None -> false

let preparedArgumentReachesWhole (index: Int) (prepared: PreparedCallReach) =
    match Ashes.Collection.Map.getInt(index)(prepared.byArgument) with
        | Some(CallParameterReach { wholeCount = count }) -> count > 0
        | None -> false

let preparedArgumentCannotKeepWhole (index: Int) (prepared: PreparedCallReach) =
    match Ashes.Collection.Map.getInt(index)(prepared.byArgument) with
        | Some(CallParameterReach { wholeCount = wholeCount, exposedWholeCount = exposedCount }) -> prepared.inputsEnumerated && wholeCount == 0 && exposedCount == 0
        | None -> false

let recursive preparedReachFlagsFrom (index: Int) (prepared: PreparedCallReach) =
    if index == prepared.parameterCount
    then []
    else preparedArgumentReaches(index)(prepared) :: preparedReachFlagsFrom(index + 1)(prepared)

let preparedCallReachFlags (prepared: PreparedCallReach) = preparedReachFlagsFrom(0)(prepared)

// The facts about a call spine's callee the argument and result rules consult, stage 0's
// ownership summary and known-label resolution of a let-bound function called by name: its
// generated label (once its body is lowered), its parameters in order, their ownership, the
// parameter reach of its result, whether the pre-lowering provenance classification proved its
// result RC-eligible, and the number of arguments the spine applies.
type CoreCalleeFacts =
    | label: Maybe(Str)
    | parameters: List(Str)
    | ownership: List((Str, ParameterOwnership))
    | reach: ResultReachState
    | preparedReach: PreparedCallReach
    | rcEligible: Bool
    | argumentCount: Int

let recursive lookupReturnedClosureLabel (label: Str) (labels: List((Str, Str))) =
    match labels with
        | [] -> None
        | (candidate, returned) :: rest ->
            if candidate == label
            then Some(returned)
            else lookupReturnedClosureLabel(label)(rest)

// The label of the stage `hops` returned closures past `label` along the recorded
// returned-closure chain, `None` where the chain is not recorded.
let recursive innermostStageLabel (hops: Int) (label: Str) (labels: List((Str, Str))) =
    if hops <= 0
    then Some(label)
    else
        match lookupReturnedClosureLabel(label)(labels) with
            | Some(next) -> innermostStageLabel(hops - 1)(next)(labels)
            | None -> None

let lookupBodyPlacement (label: Str) (placements: MapTree(Str, Bool)) = Ashes.Collection.Map.getStr(label)(placements)

// Stage 0's `TryGetCompiledFunctionResultRuntimeManaged`: whether the lowered body of the stage
// the spine's applications reach produced a reference-counted result, following the
// returned-closure chain one hop per application past the first; `None` while that body is not
// lowered or the chain is not recorded.
let compiledResultRuntimeManaged (facts: Maybe(CoreCalleeFacts)) (returnedClosureLabels: List((Str, Str))) (bodyPlacements: MapTree(Str, Bool)) =
    match facts with
        | Some(CoreCalleeFacts { label = Some(label), argumentCount = count }) ->
            match innermostStageLabel(count - 1)(label)(returnedClosureLabels) with
                | Some(stage) -> lookupBodyPlacement(stage)(bodyPlacements)
                | None -> None
        | _ -> None

// Stage 0's `TryResolveCalleeOwnershipSummary` under the provenance verdict: the spine saturates
// a callee whose result the classification proved RC-eligible.
let calleeSaturatedAndEligible (facts: Maybe(CoreCalleeFacts)) =
    match facts with
        | Some(CoreCalleeFacts { parameters = parameters, rcEligible = eligible, argumentCount = count }) -> eligible && length(parameters) == count
        | None -> false

// The ownership of the parameter at `index`, consumed past the classified chain.
let recursive parameterAtIndex (index: Int) (ownership: List((Str, ParameterOwnership))) =
    match ownership with
        | [] -> Consumed
        | (_parameter, kind) :: rest ->
            if index == 0
            then kind
            else parameterAtIndex(index - 1)(rest)

// Stage 0's `CalleeParamBorrowsOnly`: the callee's summary classifies the parameter at this
// position as borrowed.
let calleeParameterBorrows (facts: Maybe(CoreCalleeFacts)) (index: Int) =
    match facts with
        | Some(CoreCalleeFacts { ownership = ownership }) -> parameterAtIndex(index)(ownership) == Borrowed
        | None -> false

// The parameter's own entry and its component entries (`items/0`) together.
let recursive reachCountOf (parameter: Str) (entries: List(ParameterReachEntry)) =
    match entries with
        | [] -> 0
        | ParameterReachEntry { parameterName = name, reachCount = count } :: rest ->
            if name == parameter || Ashes.Text.startsWith(name)(parameter + "/")
            then count + reachCountOf(parameter)(rest)
            else reachCountOf(parameter)(rest)

// Stage 0's `ResultReaches`: the callee's result may alias the parameter at this position, whole
// or through one of its components (a matched head or field kept in the result).
let calleeResultReachesArgument (facts: Maybe(CoreCalleeFacts)) (index: Int) =
    match facts with
        | Some(CoreCalleeFacts { preparedReach = prepared }) -> preparedArgumentReaches(index)(prepared)
        | None -> false

// Stage 0's `ResultReachesWhole`: the callee's result may keep the parameter's value itself (a
// curry stage that stores it into the value it returns), so a fresh argument moves into the
// callee outright; a callee that keeps only destructured parts of it does not take the whole
// value, and the caller's release of the spine stays.
let calleeResultReachesArgumentWhole (facts: Maybe(CoreCalleeFacts)) (index: Int) =
    match facts with
        | Some(CoreCalleeFacts { preparedReach = prepared }) -> preparedArgumentReachesWhole(index)(prepared)
        | None -> false

// Stage 0's `CalleeResultCannotKeepParameterWhole`: the proof `calleeResultReachesArgumentWhole` is
// the possibility of. The callee's result never holds the argument itself, though it may hold
// parts of it, and this holds under poison too, as long as every input of the unproven construct
// was enumerated: an unknown callee applied to a parameter's destructured parts leaves the result
// unconfined while proving the list itself cannot be in it, so the caller may release the list it
// handed over instead of abandoning it to the callee.
let calleeResultCannotKeepArgumentWhole (facts: Maybe(CoreCalleeFacts)) (index: Int) =
    match facts with
        | Some(CoreCalleeFacts { preparedReach = prepared }) -> preparedArgumentCannotKeepWhole(index)(prepared)
        | None -> false
