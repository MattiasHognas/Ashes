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
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Semantics.HeapLayoutClassification.canArenaResetLayout
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.OwnershipInference.ResultReachState
import AshesCompiler.Semantics.OwnershipInference.analyzeExprReach
import AshesCompiler.Semantics.OwnershipInference.reachParam
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.Types
export (
    type CallCopyOut(..),
    type CoreCalleeFacts(..),
    value callCopyOutInstruction,
    value listHeadCopyOf,
    value calleeReach,
    value parameterAtIndex,
    value calleeParameterBorrows,
    value calleeResultReachesArgument,
    value calleeResultReachesArgumentWhole,
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

let recursive lookupBodyPlacement (label: Str) (placements: List((Str, Bool))) =
    match placements with
        | [] -> None
        | (candidate, runtimeManaged) :: rest ->
            if candidate == label
            then Some(runtimeManaged)
            else lookupBodyPlacement(label)(rest)

// Stage 0's `TryGetCompiledFunctionResultRuntimeManaged`: whether the lowered body of the stage
// the spine's applications reach produced a reference-counted result, following the
// returned-closure chain one hop per application past the first; `None` while that body is not
// lowered or the chain is not recorded.
let compiledResultRuntimeManaged (facts: Maybe(CoreCalleeFacts)) (returnedClosureLabels: List((Str, Str))) (bodyPlacements: List((Str, Bool))) =
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

let recursive reachEnvironment (parameters: List(Str)) (environment: List((Str, ResultReachState))) =
    match parameters with
        | [] -> environment
        | parameter :: rest -> reachEnvironment(rest)((parameter, reachParam(parameter)) :: environment)

// The parameter reach of a callee's result over its own body.
let calleeReach (parameters: List(Str)) (body: Expr) = analyzeExprReach(body)(reachEnvironment(parameters)([]))

// The ownership of the parameter at `index`, consumed past the classified chain.
let recursive parameterAtIndex (index: Int) (ownership: List((Str, ParameterOwnership))) =
    match ownership with
        | [] -> Consumed
        | (_parameter, kind) :: rest ->
            if index == 0
            then kind
            else parameterAtIndex(index - 1)(rest)

let recursive parameterNameAt (index: Int) (parameters: List(Str)) =
    match parameters with
        | [] -> None
        | parameter :: rest ->
            if index == 0
            then Some(parameter)
            else parameterNameAt(index - 1)(rest)

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
        | Some(CoreCalleeFacts { parameters = parameters, reach = ResultReachState { counts = counts } }) ->
            match parameterNameAt(index)(parameters) with
                | Some(parameter) -> reachCountOf(parameter)(counts) > 0
                | None -> false
        | None -> false

// The parameter's own entry alone: a whole-value reach.
let recursive wholeReachCountOf (parameter: Str) (entries: List(ParameterReachEntry)) =
    match entries with
        | [] -> 0
        | ParameterReachEntry { parameterName = name, reachCount = count } :: rest ->
            if name == parameter
            then count + wholeReachCountOf(parameter)(rest)
            else wholeReachCountOf(parameter)(rest)

// Stage 0's `ResultReachesWhole`: the callee's result may keep the parameter's value itself (a
// curry stage that stores it into the value it returns), so a fresh argument moves into the
// callee outright; a callee that keeps only destructured parts of it does not take the whole
// value, and the caller's release of the spine stays.
let calleeResultReachesArgumentWhole (facts: Maybe(CoreCalleeFacts)) (index: Int) =
    match facts with
        | Some(CoreCalleeFacts { parameters = parameters, reach = ResultReachState { counts = counts } }) ->
            match parameterNameAt(index)(parameters) with
                | Some(parameter) -> wholeReachCountOf(parameter)(counts) > 0
                | None -> false
        | None -> false
