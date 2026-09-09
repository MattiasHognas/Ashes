// The soundness gate of stage 0's `f$reuse` whole-function reuse specialization
// (`Lowering.Reuse.cs`: `IsFullyReusing`, `SpecializationRebuildsAccumulator`,
// `AccumulatorIsFullyPersistent`). A specialization rewrites its accumulator in place, so the loop
// that drives it may reclaim its arena on every back edge only when every value the specialized
// body returns already lies below the loop watermark. These predicates decide that; the
// specialization itself, its call-site qualification, and the lowering state they need stay in
// CoreLowering.ash.
//
// Invariants:
// - Reset safety is a sufficient condition, never a necessary one: an unrecognized instruction
//   shape rejects the reset, which loses the reclamation and never keeps an unsafe one.
// - The scan reads the lowered instruction list only. It names no label and trusts no origin, so
//   a specialization's own body is judged by what it allocates, not by what generated it.
// - Recursion through borrows and single-store local slots is bounded at four levels, stage 0's
//   own depth limit: a longer chain is rejected rather than followed.
// - A named-type accumulator is persistent when every constructor field is either the accumulator
//   itself or a leaf the lowering's constructor-site materialization relocates into the never-reset
//   to-space (`namedAccumulatorFieldsPersistent`). A field the materialization has no case for —
//   a list over a heap element, a closure, a resource handle — would be left pointing into
//   reclaimed scratch, so it declines the accumulator instead.

import Ashes.Collection.List.map
import AshesCompiler.Semantics.HeapLayoutClassification.canArenaResetLayout
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins.IrSourceLocation
import AshesCompiler.Semantics.ReuseDecision.ReuseDecisionReason
import AshesCompiler.Semantics.StateMachineTransform.getUsedTemps
import AshesCompiler.Semantics.Types.SemanticType
export (
    type ReuseResetSafety(..),
    value reuseResetSafety,
    value specializationRebuildsAccumulator,
    value accumulatorIsFullyPersistent,
    value namedAccumulatorFieldsPersistent,
)

// The verdict on one specialized body: whether its loop's arena reset is safe, the decision reason
// reporting records, and the rejecting instruction's location when one is known.
type ReuseResetSafety =
    | accepted: Bool
    | reason: ReuseDecisionReason
    | location: Maybe(IrSourceLocation)
    deriving {Eq, Show}

// Each instruction paired with the temps it reads, the store count of each local slot, and the
// temps each local slot's loads produce: the three tables the escape walks below query.
type ReuseResetScan =
    | uses: List((IrInstructionKind, List(Int)))
    | storeCounts: List((Int, Int))
    | slotLoads: List((Int, List(Int)))

let recursive containsTemp (temp: Int) (temps: List(Int)) =
    match temps with
        | [] -> false
        | candidate :: rest -> candidate == temp || containsTemp(temp)(rest)

let recursive countedSlot (slot: Int) (counts: List((Int, Int))) =
    match counts with
        | [] -> 0
        | (candidate, count) :: rest ->
            if candidate == slot
            then count
            else countedSlot(slot)(rest)

let recursive withSlotStore (slot: Int) (counts: List((Int, Int))) =
    match counts with
        | [] -> [(slot, 1)]
        | (candidate, count) :: rest ->
            if candidate == slot
            then (candidate, count + 1) :: rest
            else (candidate, count) :: withSlotStore(slot)(rest)

let recursive loadedTemps (slot: Int) (loads: List((Int, List(Int)))) =
    match loads with
        | [] -> []
        | (candidate, temps) :: rest ->
            if candidate == slot
            then temps
            else loadedTemps(slot)(rest)

let recursive withSlotLoad (slot: Int) (target: Int) (loads: List((Int, List(Int)))) =
    match loads with
        | [] -> [(slot, [target])]
        | (candidate, temps) :: rest ->
            if candidate == slot
            then (candidate, target :: temps) :: rest
            else (candidate, temps) :: withSlotLoad(slot)(target)(rest)

// The allocation kinds that put a value on the result heap rather than rewriting one in place.
// A body containing any of them can return something above the loop watermark, so its reset is
// rejected outright before the escape walks even run.
let forbiddenAllocationReason (kind: IrInstructionKind) =
    match kind with
        | AllocAdt(_target, _tag, _fieldCount, _tagless, _runtimeManaged) -> Some(BecauseFreshAdtAllocation)
        | AllocAdtStack(_target, _tag, _fieldCount, _tagless) -> Some(BecauseFreshStackAdtAllocation)
        | AllocStack(_target, _sizeBytes) -> Some(BecauseFreshStackAllocation)
        | ConcatStr(_target, _left, _right, _runtimeManaged) -> Some(BecauseStringConcatenationAllocation)
        | CopyOutArena(_target, _source, _sizeBytes, _runtimeManaged, _purpose, _elementType) -> Some(BecauseArenaCopyOut)
        | CopyOutList(_target, _source, _headCopy, _runtimeManaged, _purpose) -> Some(BecauseListCopyOut)
        | CopyOutClosure(_target, _source, _runtimeManaged, _purpose) -> Some(BecauseClosureCopyOut)
        | CopyOutTcoListCell(_target, _source, _headCopy, _purpose) -> Some(BecauseTcoListCellCopyOut)
        | _ -> None

let recursive findForbiddenAllocation (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | instruction :: rest ->
            match forbiddenAllocationReason(instruction.instruction) with
                | Some(reason) -> Some(ReuseResetSafety(accepted = false, reason = reason, location = instruction.location))
                | None -> findForbiddenAllocation(rest)

let recursive collectSlotDataflow (instructions: List(IrInstruction)) (counts: List((Int, Int))) (loads: List((Int, List(Int)))) =
    match instructions with
        | [] -> (counts, loads)
        | instruction :: rest ->
            match instruction.instruction with
                | StoreLocal(slot, _source) ->
                    collectSlotDataflow(rest)(withSlotStore(slot)(counts))(loads)
                | LoadLocal(target, slot) ->
                    loads
                    |> withSlotLoad(slot)(target)
                    |> collectSlotDataflow(rest)(counts)
                | _ -> collectSlotDataflow(rest)(counts)(loads)

let buildReuseResetScan (instructions: List(IrInstruction)) =
    match collectSlotDataflow(instructions)([])([]) with
        | (counts, loads) ->
            ReuseResetScan(
                uses = map(given (instruction: IrInstruction) -> (instruction.instruction, getUsedTemps(instruction.instruction)))(instructions),
                storeCounts = counts,
                slotLoads = loads
            )

let recursive readersOf (temp: Int) (uses: List((IrInstructionKind, List(Int)))) =
    match uses with
        | [] -> []
        | (kind, used) :: rest ->
            if containsTemp(temp)(used)
            then kind :: readersOf(temp)(rest)
            else readersOf(temp)(rest)

// A fresh in-body value is SAFELY CONSUMED when every reader either writes into it, reads a field
// from it, materializes it into persistent storage, captures it in a closure environment (the
// closure is judged separately), or moves it through a single-store local slot whose every load is
// itself safely consumed. Anything else — storing the raw pointer as a node field, passing it to a
// call, returning it — means per-iteration scratch could outlive the reset.
let recursive safelyConsumed (temp: Int) (depth: Int) (scan: ReuseResetScan) =
    scan.uses
    |> readersOf(temp)
    |> everyReaderConsumesSafely(temp)(depth)(scan)
and everyReaderConsumesSafely (temp: Int) (depth: Int) (scan: ReuseResetScan) (readers: List(IrInstructionKind)) =
    match readers with
        | [] -> true
        | reader :: rest ->
            if readerConsumesSafely(reader)(temp)(depth)(scan)
            then everyReaderConsumesSafely(temp)(depth)(scan)(rest)
            else false
and readerConsumesSafely (reader: IrInstructionKind) (temp: Int) (depth: Int) (scan: ReuseResetScan) =
    match reader with
        | MakeClosure(_target, _label, _environment, _size, _runtimeManaged, _returnsManaged, _acceptsManaged) -> true
        | MakeClosureStack(_target, _label, _environment, _size, _returnsManaged, _acceptsManaged) -> true
        | SetAdtField(pointer, _index, _source, _tagless) -> pointer == temp
        | GetAdtField(_target, pointer, _index, _tagless) -> pointer == temp
        | StoreMemOffset(basePointer, _offset, _source) -> basePointer == temp
        | LoadMemOffset(_target, basePointer, _offset) -> basePointer == temp
        | CopyFixedInto(_destination, source, _sizeBytes) -> source == temp
        | CopyOutArenaToSpace(_destination, source, _sizeBytes) -> source == temp
        | Borrow(target, source) -> source == temp && depth < 4 && safelyConsumed(target)(depth + 1)(scan)
        | StoreLocal(slot, _source) ->
            depth < 4 && countedSlot(slot)(scan.storeCounts) == 1 && everyLoadSafelyConsumed(loadedTemps(slot)(scan.slotLoads))(depth + 1)(scan)
        | _ -> false
and everyLoadSafelyConsumed (targets: List(Int)) (depth: Int) (scan: ReuseResetScan) =
    match targets with
        | [] -> true
        | target :: rest ->
            if safelyConsumed(target)(depth)(scan)
            then everyLoadSafelyConsumed(rest)(depth)(scan)
            else false

// A closure temp is CONSUMED AS A CALL TARGET — so it never escapes into a returned cell — when
// every reader either calls it as the callee or moves it through a borrow or a single-store local
// slot whose loads are themselves consumed as call targets. Passing it as a call ARGUMENT is
// rejected: it could be captured or returned from there.
let recursive closureConsumedAsCallTarget (temp: Int) (depth: Int) (scan: ReuseResetScan) =
    scan.uses
    |> readersOf(temp)
    |> everyReaderCallsTarget(temp)(depth)(scan)
and everyReaderCallsTarget (temp: Int) (depth: Int) (scan: ReuseResetScan) (readers: List(IrInstructionKind)) =
    match readers with
        | [] -> true
        | reader :: rest ->
            if readerCallsTarget(reader)(temp)(depth)(scan)
            then everyReaderCallsTarget(temp)(depth)(scan)(rest)
            else false
and readerCallsTarget (reader: IrInstructionKind) (temp: Int) (depth: Int) (scan: ReuseResetScan) =
    match reader with
        | CallClosure(_target, closureTemp, _argument, _argumentFlag) -> closureTemp == temp
        | Borrow(target, source) -> source == temp && depth < 4 && closureConsumedAsCallTarget(target)(depth + 1)(scan)
        | StoreLocal(slot, _source) ->
            depth < 4 && countedSlot(slot)(scan.storeCounts) == 1 && everyLoadCallsTarget(loadedTemps(slot)(scan.slotLoads))(depth + 1)(scan)
        | _ -> false
and everyLoadCallsTarget (targets: List(Int)) (depth: Int) (scan: ReuseResetScan) =
    match targets with
        | [] -> true
        | target :: rest ->
            if closureConsumedAsCallTarget(target)(depth)(scan)
            then everyLoadCallsTarget(rest)(depth)(scan)
            else false

let acceptedResetSafety unit = ReuseResetSafety(accepted = true, reason = BecauseNoResetInvalidatingAllocation, location = None)

let rejectedResetSafety (reason: ReuseDecisionReason) (location: Maybe(IrSourceLocation)) = ReuseResetSafety(accepted = false, reason = reason, location = location)

// Every raw allocation must be a closure environment and every closure a call target: those are
// the recursion scaffolding a specialized body legitimately builds, dead after the call and
// reclaimable. Any other escaping allocation disqualifies the reset.
let recursive allocationsConsumed (instructions: List(IrInstruction)) (scan: ReuseResetScan) =
    match instructions with
        | [] -> acceptedResetSafety(Unit)
        | instruction :: rest ->
            match instruction.instruction with
                | Alloc(target, _sizeBytes, _runtimeManaged) ->
                    if safelyConsumed(target)(0)(scan)
                    then allocationsConsumed(rest)(scan)
                    else rejectedResetSafety(BecauseRawAllocationMayEscape)(instruction.location)
                | MakeClosure(target, _label, _environment, _size, _runtimeManaged, _returnsManaged, _acceptsManaged) ->
                    if closureConsumedAsCallTarget(target)(0)(scan)
                    then allocationsConsumed(rest)(scan)
                    else rejectedResetSafety(BecauseClosureMayEscape)(instruction.location)
                | MakeClosureStack(target, _label, _environment, _size, _returnsManaged, _acceptsManaged) ->
                    if closureConsumedAsCallTarget(target)(0)(scan)
                    then allocationsConsumed(rest)(scan)
                    else rejectedResetSafety(BecauseStackClosureMayEscape)(instruction.location)
                | _ -> allocationsConsumed(rest)(scan)

// Whether the arena reset of the loop driving a specialized body is safe: no fresh result-heap
// allocation anywhere, and every raw allocation and closure consumed without escaping.
let reuseResetSafety (instructions: List(IrInstruction)) =
    match findForbiddenAllocation(instructions) with
        | Some(rejection) -> rejection
        | None ->
            instructions
            |> buildReuseResetScan
            |> allocationsConsumed(instructions)

// The type of the last parameter of a curried function type applied to `argumentCount` arguments,
// paired with the result type at that point. `None` when the type has fewer parameters than that.
let recursive appliedAccumulatorAndResult (functionType: SemanticType) (argumentCount: Int) (accumulator: Maybe(SemanticType)) =
    if argumentCount == 0
    then Some((accumulator, functionType))
    else
        match functionType with
            | SemFunction(argument, result, _effects) -> appliedAccumulatorAndResult(result)(argumentCount - 1)(Some(argument))
            | _ -> None

// Whether a specializable function actually rebuilds its accumulator: its result type, after all
// its arguments are applied, is the same list or named type as its last parameter. A rewriter
// (`MapTree -> MapTree`) qualifies; a reader (`MapTree -> Maybe`) does not, and routing one through
// the specialization would allocate its result cell where nothing reclaims it.
let specializationRebuildsAccumulator (functionType: SemanticType) (argumentCount: Int) =
    match appliedAccumulatorAndResult(functionType)(argumentCount)(None) with
        | Some((Some(SemList(_element)), SemList(_resultElement))) -> true
        | Some((Some(SemNamed(_symbol, parameterName, _arguments)), SemNamed(_resultSymbol, resultName, _resultArguments))) -> parameterName == resultName
        | _ -> false

// Whether a rebuilt accumulator references only memory the back edge never reclaims. A list of a
// copy-type element carries no heap leaf at all, so its cells are the whole graph; a list over a
// heap element declines, since the cells are then not the whole graph. A named type goes through
// `namedAccumulatorFieldsPersistent` instead. The argument must already be resolved against the
// current substitution.
let accumulatorIsFullyPersistent (accumulatorType: SemanticType) =
    match accumulatorType with
        | SemList(element) -> canArenaResetLayout(element)
        | _ -> false

// The leaf half of stage 0's `IsReuseMaterializableFieldType`: a constructor field the lowering's
// materialization can relocate into to-space, where the loop's per-iteration reset never reaches,
// without synthesizing a copier for it. A copy type is inline and needs nothing; a string or bytes
// value is copied by its length; a tuple of copy types is a fixed-size shallow copy. The two shapes
// that do need a synthesized copier are the caller's `relocatable` half.
let recursive reuseMaterializableFieldType (fieldType: SemanticType) =
    match fieldType with
        | SemString -> true
        | SemBytes -> true
        | SemTuple(elements) -> everyCopyTypeElement(elements)
        | other -> canArenaResetLayout(other)
and everyCopyTypeElement (elements: List(SemanticType)) =
    match elements with
        | [] -> true
        | element :: rest -> canArenaResetLayout(element) && everyCopyTypeElement(rest)

// The named-ADT half of stage 0's `AccumulatorIsFullyPersistent`: every constructor field is either
// the accumulator type itself (a recursive child, rewritten in place) or a leaf the materialization
// makes persistent. `relocatable` answers for the shapes whose materialization needs the type
// environment — a list of strings and a to-space-copyable ADT, both relocated through a synthesized
// copier — which this module has no access to; a field neither half admits could point into
// per-iteration scratch with nothing to relocate it, and declines the accumulator. `fieldTypes` is
// every constructor's fields of the named type, already resolved.
let recursive everyAccumulatorFieldPersistable (accumulatorName: Str) relocatable (fieldTypes: List(SemanticType)) =
    match fieldTypes with
        | [] -> true
        | (SemNamed(_symbol, fieldName, _arguments) as fieldType) :: rest -> (fieldName == accumulatorName || reuseMaterializableFieldType(fieldType) || relocatable(fieldType)) && everyAccumulatorFieldPersistable(accumulatorName)(relocatable)(rest)
        | fieldType :: rest -> (reuseMaterializableFieldType(fieldType) || relocatable(fieldType)) && everyAccumulatorFieldPersistable(accumulatorName)(relocatable)(rest)

let namedAccumulatorFieldsPersistent (accumulatorName: Str) relocatable (fieldTypes: List(SemanticType)) = everyAccumulatorFieldPersistable(accumulatorName)(relocatable)(fieldTypes)
