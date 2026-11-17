// Complete pure-Ashes IR optimization pipeline.
//
// Invariants:
// - Optimization is semantics-preserving and invisible to user programs.
// - Pass ordering matches stage 0:
//   1. Compile-time evaluation (IrCompileTimeEval)
//   2. Trivial ownership-copy elision (erased RcDup, copy-type / single-use Borrow)
//   3. Runtime RcDup sinking into branch diamonds
//   4. Adjacent runtime RcDup / RcDrop pair fusion
//   5. Known closure devirtualization (CallClosure -> CallKnown), also through a local slot
//      written by exactly one StoreLocal, dropping the load it made dead and the scope-exit
//      resource cleanup of a stack closure that never received a dropper
//   6. Constant propagation and folding (arithmetic, bitwise, comparison; temp and local-slot facts
//      with a true meet over every predecessor edge at a label once all edges are observed, and a
//      conservative clear at a label with a not-yet-observed backward edge; a JumpIfFalse whose
//      condition is known folds to a fall-through or an unconditional Jump, and a SwitchTag whose
//      tag is known folds to a Jump to the taken case)
//   7. Identity elimination and strength reduction (x+0, x-0, x*0, x*1, x*2, x/1), followed by a
//      second ownership-copy elision: an identity rewrites into a Borrow copy rather than
//      retargeting its uses, and the elision that already ran never revisits that new copy
//   8. Local common-subexpression elimination within one straight-line block (reset at every
//      label): a duplicate GetAdtField read, or a duplicate CallKnown of a function the
//      compile-time-evaluation purity oracle proves pure, forwards to the first occurrence through
//      a Borrow copy; operands are canonicalized through a LoadLocal/StoreLocal/Borrow/RcDup alias
//      map (with the function's own env/arg slots seeded to a stable identity) before keying the
//      caches, which any instruction that could write through an existing pointer invalidates,
//      while arena and stack bookkeeping never does, and a SetAdtField through a pointer
//      allocated in the same block populates instead (store-to-load forwarding); followed by a
//      third ownership-copy elision that forwards the copies it introduced
//   9. Control-flow simplification (jump threading through empty labels, unreferenced-label
//      removal, redundant fall-through Jump elision) iterated to a fixed point with unreachable
//      code elimination (after Jump, Return, SwitchTag; a label with no remaining branch reference
//      inside an unreachable region is dropped with its body)
//   10. Dead code elimination (unused LoadConst, StoreLocal, MakeClosure)
//   11. Erased RcDrop marker elision
//   12. Program-level captured-closure devirtualization: a CallClosure through a LoadEnv slot
//       becomes a direct CallKnown over the closure object's environment word when every creation
//       site of the enclosing function's environment stores the same closure label into that slot
//       (resolved through single-definition temps, single-store local slots, Borrow copies,
//       known-returned call results, and the creating function's own captured slots, to a
//       whole-program fixpoint)
//   13. Program-level returned-closure devirtualization: a whole-program least fixpoint of the
//       functions whose every Return is one heap MakeClosure label (directly, or transitively
//       through a CallKnown to a function already proven), then a per-function local fixpoint
//       rewriting each CallClosure on such a call's result into an environment-word read plus
//       a direct CallKnown, so a curry of any depth resolves fully
//   14. Currying-stage inlining: a direct call of a stage that only copies its captures and its
//       argument into a fresh environment and returns the next stage's closure, followed by the
//       environment-word read and the direct call of that next stage, becomes a caller-frame
//       AllocStack filled directly plus the next call over it, iterated to a fixpoint per function
//   15. Closure environment scalarization for one or two scalar captures: a devirtualized call
//       of a stack closure whose 8- or 16-byte environment is filled by one store per word and
//       used nowhere else passes the captured words directly in the call's env word and (when
//       the call carries no ownership flag) its flag word, to a generated callee variant that
//       reads them as raw parameters, so the environment allocation disappears
//   16. Returned-closure devirtualization again, so a call made direct by the passes above is
//       visible to the non-allocation summary
//   17. Interprocedural redundant arena bracket elimination (whole-function and straight-line regions)
//   18. String-concatenation chain folding (last, over the whole program): a left-nested ConcatStr
//       chain whose intermediates are each used once becomes one ConcatStrN, declined whenever an
//       arena or stack bracket, a label, or a branch lies between the innermost part and the root

import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.Ir
import Ashes.Collection.Map
import Ashes.Collection.Map.MapTree
import AshesCompiler.Semantics.IrCompileTimeEval
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrInstructionTemps
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.StateMachineTransform
import AshesCompiler.Semantics.Types
export (
    type IrOptimizerOptions(..),
    value defaultOptimizerOptions,
    value optimizeIrProgram,
    value optimizeIrProgramWithOptions,
    value optimizeIrFunction,
    value optimizeIrFunctionWithEvaluable,
)

type IrOptimizerOptions =
    | enableCompileTimeEval: Bool
    | enableInlining: Bool
    | enableDeadCodeElision: Bool
    | enableIdentityReduction: Bool

// Constant facts carried by the folding pass. Temps are single-assignment, so a temp fact never
// changes once recorded; a local slot is ordinary mutable storage, so a store of an unknown or
// non-scalar value kills its fact and a slot holds at most one of Int/Float/Bool at a time.
// Every change to a table is journaled in `touched` as (stamp, table, key), newest first, with
// stamps unique across one scan: two fact sets that share a journal entry share everything recorded
// before it, so their meet only has to look at the keys touched after it.
type FoldFacts =
    | ints: MapTree(Int, Maybe(Int))
    | floats: MapTree(Int, Maybe(Float))
    | bools: MapTree(Int, Maybe(Bool))
    | localInts: MapTree(Int, Maybe(Int))
    | localFloats: MapTree(Int, Maybe(Float))
    | localBools: MapTree(Int, Maybe(Bool))
    | touched: List((Int, Int, Int))
    | stamp: Int

// Local common-subexpression state for one straight-line block. Cache values are always real,
// already-emitted temps; cache keys are canonical identities, which may be the negative sentinel
// of a function's own env/arg slot (no visible defining instruction) and so must never be emitted.
type LocalCseState =
    | fieldCache: MapTree(Int, MapTree(Int, IrTemp))
    | callCache: MapTree(Str, List(((Int, Int, Int, Bool), IrTemp)))
    | valueOf: MapTree(Int, Maybe(Int))
    | slotValue: MapTree(Int, Int)
    | freshPointers: MapTree(Int, Bool)

// Scalarization bookkeeping threaded through every caller: the variant label generated per
// callee and capture count (None once a callee proved ineligible), the variants to append to the
// program, and the counter that keeps generated labels unique.
type ScalarizeState =
    | variantByCallee: MapTree(Str, MapTree(Int, Maybe(Str)))
    | newFunctions: List(IrFunction)
    | counter: Int

// Per-function definition facts for closure devirtualization: how often each temp is defined and
// used, the single defining instruction of each once-defined temp, how often each local slot is
// stored and from which temp, and the closure temps that received a resource dropper.
type ClosureDefinitionFacts =
    | defCounts: MapTree(Int, Int)
    | useCounts: MapTree(Int, Int)
    | singleDefs: MapTree(Int, IrInstructionKind)
    | storeCountBySlot: MapTree(Int, Int)
    | singleStoreSourceBySlot: MapTree(Int, Int)
    | closureTempsWithDropper: MapTree(Int, Bool)

// What one closure creation site stores into one environment word, resolved as far as the creating
// function alone allows: a closure of a known label, the creating function's own captured word
// (its label and word index), or nothing provable.
type CaptureSource =
    | SourceLabel(Str)
    | SourceEnvWord(Str, Int)
    | SourceUnknown

// The outcome of resolving the closure label a captured word holds: proven, still waiting on a
// slot of the creating function that the fixpoint has not resolved yet, or unresolvable.
type CaptureResolution =
    | CaptureKnown(Str)
    | CapturePending
    | CaptureUnknown

// A currying stage's shape: its environment size, one (offset, capture index) per environment
// word where -1 stands for the stage's own argument, and the label of the closure it returns.
type CurryingStage =
    | envSizeBytes: Int
    | stores: List((Int, Int))
    | nextLabel: Str

// The instruction-by-instruction state of matching a stage body: the environment temp and size
// (-1 before the allocation), the closure temp (-1 before the construction) and its label, the
// capture index each loaded temp carries (-1 for the stage's own argument), and the stores seen
// in reverse order.
type CurryingStageScan =
    | scanEnvTemp: IrTemp
    | scanEnvSize: Int
    | scanClosureTemp: IrTemp
    | scanNextLabel: Maybe(Str)
    | sourceIndexByTemp: List((IrTemp, Int))
    | scanStores: List((Int, Int))

let defaultOptimizerOptions =
    IrOptimizerOptions(
        enableCompileTimeEval = true,
        enableInlining = true,
        enableDeadCodeElision = true,
        enableIdentityReduction = true
    )

// The two short tables of the currying-stage scan stay lists: a stage body is a handful of
// instructions. The Int key compares directly instead of through an `Eq` dictionary.
let recursive lookupTempAssociation (key: Int) entries =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupTempAssociation(key)(tail)

let recursive setTempAssociation (key: Int) value entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setTempAssociation(key)(value)(tail)

let recursive intListContains (item: Int) (xs: List(Int)) =
    match xs with
        | [] -> false
        | head :: tail ->
            if head == item
            then true
            else intListContains(item)(tail)

let recursive strListContains (item: Str) (xs: List(Str)) =
    match xs with
        | [] -> false
        | head :: tail ->
            if head == item
            then true
            else strListContains(item)(tail)

// The analysis tables are maps: a table that lives across a whole function, or across the whole
// program, is read and extended once per instruction, so a list scanned per lookup or rebuilt per
// insert costs the square of the function's size.
let intGet (key: Int) table = Ashes.Collection.Map.getInt(key)(table)

let intSet (key: Int) value table = Ashes.Collection.Map.setInt(key)(value)(table)

let intHas (key: Int) table =
    match Ashes.Collection.Map.getInt(key)(table) with
        | Some(_) -> true
        | None -> false

let strGet (key: Str) table = Ashes.Collection.Map.getStr(key)(table)

let strSet (key: Str) value table = Ashes.Collection.Map.setStr(key)(value)(table)

let strHas (key: Str) table =
    match Ashes.Collection.Map.getStr(key)(table) with
        | Some(_) -> true
        | None -> false

// A table whose keys can be forgotten holds None under a forgotten key: the map has no removal,
// and forgetting this way never rebuilds it.
let optGet (key: Int) table =
    match Ashes.Collection.Map.getInt(key)(table) with
        | Some(known) -> known
        | None -> None

let optSet (key: Int) value table = Ashes.Collection.Map.setInt(key)(Some(value))(table)

let optForget (key: Int) table =
    match Ashes.Collection.Map.getInt(key)(table) with
        | Some(Some(_)) -> Ashes.Collection.Map.setInt(key)(None)(table)
        | _ -> table

let bumpInt (key: Int) table =
    match Ashes.Collection.Map.getInt(key)(table) with
        | Some(count) -> Ashes.Collection.Map.setInt(key)(count + 1)(table)
        | None -> Ashes.Collection.Map.setInt(key)(1)(table)

let bumpStr (key: Str) table =
    match Ashes.Collection.Map.getStr(key)(table) with
        | Some(count) -> Ashes.Collection.Map.setStr(key)(count + 1)(table)
        | None -> Ashes.Collection.Map.setStr(key)(1)(table)

let recursive bumpAll (keys: List(Int)) table =
    match keys with
        | [] -> table
        | key :: rest ->
            table
            |> bumpInt(key)
            |> bumpAll(rest)

// Adds every key to a set, leaving the set untouched for a key it already holds.
let recursive addAllToSet (keys: List(Int)) table =
    match keys with
        | [] -> table
        | key :: rest ->
            if intHas(key)(table)
            then addAllToSet(rest)(table)
            else
                table
                |> intSet(key)(true)
                |> addAllToSet(rest)

let recursive setAllTo (keys: List(Int)) value table =
    match keys with
        | [] -> table
        | key :: rest ->
            table
            |> intSet(key)(value)
            |> setAllTo(rest)(value)

let recursive resolveTemp (remap: MapTree(Int, Int)) (temp: Int) =
    match intGet(temp)(remap) with
        | None -> temp
        | Some(resolved) -> resolveTemp(remap)(resolved)

// Rewrites an instruction's source temps through `remap`, leaving its defined temps alone: a
// remapped temp is only ever an elided `Borrow`/`RcDup` destination, defined nowhere else, and
// every operand of every kind flows through `mapInstructionTemps`. An empty remap rewrites nothing.
let remapSourceTemps inst (remap: MapTree(Int, Int)) =
    if Ashes.Collection.Map.isEmpty(remap)
    then inst
    else
        let defined = getDefinedTemps(inst)
        in
            mapInstructionTemps(given (temp) ->
                if intListContains(temp)(defined)
                then temp
                else resolveTemp(remap)(temp))(inst)

let recursive countUses instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            acc
            |> bumpAll(getUsedTemps(inst))
            |> countUses(tail)

// How often each temp is used.
let useCountsOf instructions = countUses(instructions)(Ashes.Collection.Map.empty)

let recursive collectCopyTypeProducers instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            match inst with
                | LoadConstInt(t, _) ->
                    acc
                    |> intSet(t)(true)
                    |> collectCopyTypeProducers(tail)
                | LoadConstFloat(t, _) ->
                    acc
                    |> intSet(t)(true)
                    |> collectCopyTypeProducers(tail)
                | LoadConstBool(t, _) ->
                    acc
                    |> intSet(t)(true)
                    |> collectCopyTypeProducers(tail)
                | _ -> collectCopyTypeProducers(tail)(acc)

let recursive hasElidableCopy instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = RcDup(_, _, false, _) } :: _ -> true
        | IrInstruction { instruction = Borrow(_, _) } :: _ -> true
        | _ :: tail -> hasElidableCopy(tail)

// A body with no erased RcDup and no Borrow has nothing to elide and is returned as it is.
let elideTrivialOwnershipCopies instructions =
    if hasElidableCopy(instructions)
    then
        let copyTypes = collectCopyTypeProducers(instructions)(Ashes.Collection.Map.empty)
        in
            let useCounts = useCountsOf(instructions)
            in
                let recursive buildRemap insts (remap: MapTree(Int, Int)) =
                    match insts with
                        | [] -> remap
                        | IrInstruction { instruction = inst } :: tail ->
                            match inst with
                                | RcDup(dest, src, false, _) ->
                                    let resolvedSrc = resolveTemp(remap)(src)
                                    in
                                        remap
                                        |> intSet(dest)(resolvedSrc)
                                        |> buildRemap(tail)
                                | Borrow(dest, src) ->
                                    let resolvedSrc = resolveTemp(remap)(src)
                                    in
                                        let isCopy = intHas(resolvedSrc)(copyTypes)
                                        in
                                            let isSingleUse =
                                                match intGet(dest)(useCounts) with
                                                    | Some(c) -> c <= 1
                                                    | None -> true
                                            in
                                                let isEligible =
                                                    if isCopy
                                                    then true
                                                    else isSingleUse
                                                in
                                                    if isEligible
                                                    then
                                                        remap
                                                        |> intSet(dest)(resolvedSrc)
                                                        |> buildRemap(tail)
                                                    else buildRemap(tail)(remap)
                                | _ -> buildRemap(tail)(remap)
                in
                    let remap = buildRemap(instructions)(Ashes.Collection.Map.empty)
                    in
                        let recursive applyRemap insts acc =
                            match insts with
                                | [] -> reverse(acc)
                                | (IrInstruction { instruction = inst, location = loc } as irInst) :: tail ->
                                    match inst with
                                        | RcDup(dest, _, false, _) ->
                                            if intHas(dest)(remap)
                                            then applyRemap(tail)(acc)
                                            else applyRemap(tail)(irInst :: acc)
                                        | Borrow(dest, _) ->
                                            if intHas(dest)(remap)
                                            then applyRemap(tail)(acc)
                                            else applyRemap(tail)(irInst :: acc)
                                        | _ ->
                                            let remapped =
                                                IrInstruction(
                                                    instruction = remapSourceTemps(inst)(remap),
                                                    location = loc
                                                )
                                            in applyRemap(tail)(remapped :: acc)
                        in applyRemap(instructions)([])
    else instructions

let recursive hasAdjacentRuntimeRcPair instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = RcDup(_, _, true, _) } :: IrInstruction { instruction = RcDrop(_, _, _, true, _, _) } :: _ -> true
        | _ :: tail -> hasAdjacentRuntimeRcPair(tail)

// The position of the last instruction that uses each temp.
let recursive lastUsePositions instructions (position: Int) table =
    match instructions with
        | [] -> table
        | IrInstruction { instruction = inst } :: tail ->
            table
            |> setAllTo(getUsedTemps(inst))(position)
            |> lastUsePositions(tail)(position + 1)

let isUsedFrom (position: Int) (temp: Int) lastUses =
    match intGet(temp)(lastUses) with
        | Some(last) -> last >= position
        | None -> false

// Only an adjacent runtime RcDup/RcDrop pair ever changes anything, so a body without one is
// returned as it is. `position` is the index of the head of `insts` in the original body: a
// duplicate is still needed when its last use lies past the pair.
let fuseAdjacentRuntimeRcPairs instructions =
    if hasAdjacentRuntimeRcPair(instructions)
    then
        let lastUses = lastUsePositions(instructions)(0)(Ashes.Collection.Map.empty)
        in
            let recursive fusePass insts (position: Int) (remap: MapTree(Int, Int)) acc =
                match insts with
                    | [] -> reverse(acc)
                    | (IrInstruction { instruction = RcDup(dest, src, true, borrowFlag), location = loc1 } as dupInst) :: (IrInstruction { instruction = RcDrop(dropSrc, dropName, dropSlot, true, dropBorrow, dropOrigin), location = loc2 } as dropInst) :: tail ->
                        let remappedDup = remapSourceTemps(dupInst.instruction)(remap)
                        in
                            let remappedDrop = remapSourceTemps(dropInst.instruction)(remap)
                            in
                                match (remappedDup, remappedDrop) with
                                    | (RcDup(rDest, rSrc, true, _), RcDrop(rDropSrc, _, _, true, _, _)) ->
                                        if rDropSrc == rDest
                                        then
                                            if !isUsedFrom(position + 2)(rDest)(lastUses)
                                            then fusePass(tail)(position + 2)(remap)(acc)
                                            else
                                                fusePass(dropInst :: tail)(position + 1)(remap)(
                                                    IrInstruction(instruction = remappedDup, location = loc1) :: acc
                                                )
                                        else
                                            if rDropSrc == rSrc
                                            then
                                                let nextRemap = intSet(rDest)(rSrc)(remap)
                                                in fusePass(tail)(position + 2)(nextRemap)(acc)
                                            else
                                                fusePass(dropInst :: tail)(position + 1)(remap)(
                                                    IrInstruction(instruction = remappedDup, location = loc1) :: acc
                                                )
                                    | _ ->
                                        fusePass(dropInst :: tail)(position + 1)(remap)(
                                            IrInstruction(instruction = remappedDup, location = loc1) :: acc
                                        )
                    | IrInstruction { instruction = inst, location = loc } :: tail ->
                        let remapped =
                            IrInstruction(
                                instruction = remapSourceTemps(inst)(remap),
                                location = loc
                            )
                        in fusePass(tail)(position + 1)(remap)(remapped :: acc)
            in fusePass(instructions)(0)(Ashes.Collection.Map.empty)([])
    else instructions

let recursive countDefinitions instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            acc
            |> bumpAll(getDefinedTemps(inst))
            |> countDefinitions(tail)

// How often each temp is defined.
let defCountsOf instructions = countDefinitions(instructions)(Ashes.Collection.Map.empty)

// Every single-defined temp mapped to its defining instruction.
let recursive collectSingleDefiningInstructions instructions defCounts acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            let recursive addDefs (ds: List(Int)) entries =
                match ds with
                    | [] -> entries
                    | d :: dTail ->
                        if intGet(d)(defCounts) == Some(1)
                        then
                            entries
                            |> intSet(d)(inst)
                            |> addDefs(dTail)
                        else addDefs(dTail)(entries)
            in
                acc
                |> addDefs(getDefinedTemps(inst))
                |> collectSingleDefiningInstructions(tail)(defCounts)

let singleDefsOf instructions defCounts = collectSingleDefiningInstructions(instructions)(defCounts)(Ashes.Collection.Map.empty)

// Known-closure devirtualization. A CallClosure whose closure temp is produced by a
// MakeClosure/MakeClosureStack with a statically known label becomes a direct CallKnown of that
// label with the closure's captured environment pointer. Only single-definition temps are
// rewritten (a unique definition dominates every use in well-formed IR), and the env temp must
// itself be single-definition so its value at the call equals the value stored into the closure.
// A local slot written by exactly one StoreLocal holds that store's value at every load, since
// lowering only reads a binding's slot inside the binding's own scope, after the store; that is
// what lets a let-bound local helper, whose call always goes through a StoreLocal/LoadLocal round
// trip, resolve to its MakeClosure like an immediately-applied lambda does.
let recursive countStoresBySlot instructions stores sources =
    match instructions with
        | [] -> (stores, sources)
        | IrInstruction { instruction = StoreLocal(slot, source) } :: tail ->
            sources
            |> intSet(slot)(source)
            |> countStoresBySlot(tail)(bumpInt(slot)(stores))
        | _ :: tail -> countStoresBySlot(tail)(stores)(sources)

// The closure object's resource-dropper word: {code, env, packed size/ownership, dropper}.
let closureDropperOffsetBytes = 24

let recursive collectClosureTempsWithDropper instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = StoreMemOffset(basePtr, offset, _) } :: tail ->
            if offset == closureDropperOffsetBytes
            then
                acc
                |> intSet(basePtr)(true)
                |> collectClosureTempsWithDropper(tail)
            else collectClosureTempsWithDropper(tail)(acc)
        | _ :: tail -> collectClosureTempsWithDropper(tail)(acc)

// A function's environment and argument arrive in slots 0 and 1 with no StoreLocal of their
// own; each counts as one store, so a slot a loop's back edge writes once more is not a
// single-store slot and a load before that write is never resolved to the closure it stores.
let parameterSlotsOf (fn: IrFunction) =
    if fn.hasEnvAndArgParams
    then [0, 1]
    else []

let recursive implicitParameterStores (slots: List(Int)) table =
    match slots with
        | [] -> table
        | slot :: rest ->
            table
            |> intSet(slot)(1)
            |> implicitParameterStores(rest)

let collectClosureDefinitionFacts (parameterSlots: List(Int)) instructions =
    (let defCounts = defCountsOf(instructions)
    in
        match countStoresBySlot(instructions)(implicitParameterStores(parameterSlots)(Ashes.Collection.Map.empty))(Ashes.Collection.Map.empty) with
            | (storeCounts, storeSources) ->
                ClosureDefinitionFacts(
                    defCounts = defCounts,
                    useCounts = useCountsOf(instructions),
                    singleDefs = singleDefsOf(instructions)(defCounts),
                    storeCountBySlot = storeCounts,
                    singleStoreSourceBySlot = storeSources,
                    closureTempsWithDropper = collectClosureTempsWithDropper(instructions)(Ashes.Collection.Map.empty)
                ))

// The instruction that produced a closure temp, seen through a single-store local slot; the flag
// says whether the definition was reached through such a slot load.
let resolveClosureDefinition (facts: ClosureDefinitionFacts) (closureTemp: IrTemp) =
    match intGet(closureTemp)(facts.singleDefs) with
        | None -> None
        | Some(def) ->
            match def with
                | LoadLocal(_, slot) ->
                    if intGet(slot)(facts.storeCountBySlot) == Some(1)
                    then
                        match intGet(slot)(facts.singleStoreSourceBySlot) with
                            | Some(storedTemp) ->
                                match intGet(storedTemp)(facts.singleDefs) with
                                    | Some(storedDef) -> Some((storedDef, true))
                                    | None -> Some((def, false))
                            | None -> Some((def, false))
                    else Some((def, false))
                | _ -> Some((def, false))

// A load of a single-store slot holding a stack closure that never had a resource dropper
// installed: a resource cleanup of that value is a runtime no-op (the dropper word is zero).
let isDropperFreeStackClosureSlotLoad (facts: ClosureDefinitionFacts) (temp: IrTemp) =
    match resolveClosureDefinition(facts)(temp) with
        | Some((MakeClosureStack(target, _, _, _, _, _, _), true)) -> !intHas(target)(facts.closureTempsWithDropper)
        | _ -> false

let tryBuildKnownCall (facts: ClosureDefinitionFacts) dest argTemp flagTemp definition =
    match definition with
        | MakeClosure(_, fnLabel, envTemp, _, _, _, _, _) ->
            if intGet(envTemp)(facts.defCounts) == Some(1)
            then
                false
                |> CallKnown(dest)(fnLabel)(envTemp)(argTemp)(flagTemp)
                |> Some
            else None
        | MakeClosureStack(_, fnLabel, envTemp, envSize, _, _, _) ->
            if intGet(envTemp)(facts.defCounts) == Some(1)
            then
                envSize > 0
                |> CallKnown(dest)(fnLabel)(envTemp)(argTemp)(flagTemp)
                |> Some
            else None
        | _ -> None

let recursive removeDeadSlotLoads instructions deadTemps acc =
    match instructions with
        | [] -> reverse(acc)
        | (IrInstruction { instruction = LoadLocal(target, _) } as irInst) :: tail ->
            if intHas(target)(deadTemps)
            then removeDeadSlotLoads(tail)(deadTemps)(acc)
            else removeDeadSlotLoads(tail)(deadTemps)(irInst :: acc)
        | head :: tail -> removeDeadSlotLoads(tail)(deadTemps)(head :: acc)

// A slot load whose only use was a call rewritten here (or a removed no-op cleanup) is dead
// afterwards; dropping it lets the slot's store and the closure construction die in the ordinary
// dead-code sweep.
let recursive devirtualizeKnownClosureCallsPass (facts: ClosureDefinitionFacts) insts deadLoads acc =
    match insts with
        | [] -> (reverse(acc), deadLoads)
        | (IrInstruction { instruction = CleanupResource(source, "Function", None) } as irInst) :: tail ->
            if isDropperFreeStackClosureSlotLoad(facts)(source)
            then
                if intGet(source)(facts.useCounts) == Some(1)
                then
                    devirtualizeKnownClosureCallsPass(facts)(tail)(intSet(source)(true)(deadLoads))(acc)
                else devirtualizeKnownClosureCallsPass(facts)(tail)(deadLoads)(acc)
            else devirtualizeKnownClosureCallsPass(facts)(tail)(deadLoads)(irInst :: acc)
        | (IrInstruction { instruction = CallClosure(dest, closureTemp, argTemp, flagTemp), location = loc } as irInst) :: tail ->
            match resolveClosureDefinition(facts)(closureTemp) with
                | Some((definition, throughSlot)) ->
                    match tryBuildKnownCall(facts)(dest)(argTemp)(flagTemp)(definition) with
                        | Some(known) ->
                            let nextDead =
                                if throughSlot
                                then
                                    if intGet(closureTemp)(facts.useCounts) == Some(1)
                                    then intSet(closureTemp)(true)(deadLoads)
                                    else deadLoads
                                else deadLoads
                            in devirtualizeKnownClosureCallsPass(facts)(tail)(nextDead)(IrInstruction(instruction = known, location = loc) :: acc)
                        | None -> devirtualizeKnownClosureCallsPass(facts)(tail)(deadLoads)(irInst :: acc)
                | None -> devirtualizeKnownClosureCallsPass(facts)(tail)(deadLoads)(irInst :: acc)
        | head :: tail -> devirtualizeKnownClosureCallsPass(facts)(tail)(deadLoads)(head :: acc)

let recursive hasClosureCallOrCleanup instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CallClosure(_, _, _, _) } :: _ -> true
        | IrInstruction { instruction = CleanupResource(_, "Function", None) } :: _ -> true
        | _ :: tail -> hasClosureCallOrCleanup(tail)

// The definition facts are only ever consulted at a closure call or a closure cleanup, so a body
// with neither is returned as it is.
let devirtualizeKnownClosureCalls (parameterSlots: List(Int)) instructions =
    if hasClosureCallOrCleanup(instructions)
    then
        let facts = collectClosureDefinitionFacts(parameterSlots)(instructions)
        in
            match devirtualizeKnownClosureCallsPass(facts)(instructions)(Ashes.Collection.Map.empty)([]) with
                | (rewritten, deadLoads) ->
                    if Ashes.Collection.Map.isEmpty(deadLoads)
                    then rewritten
                    else removeDeadSlotLoads(rewritten)(deadLoads)([])
    else instructions

let switchCaseTag (switchCase: IrSwitchCase) = switchCase.tag

let switchCaseLabel (switchCase: IrSwitchCase) = switchCase.label

let recursive countBranchRefsToLabels instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            match inst with
                | Jump(target) ->
                    acc
                    |> bumpStr(target)
                    |> countBranchRefsToLabels(tail)
                | JumpIfFalse(_, target) ->
                    acc
                    |> bumpStr(target)
                    |> countBranchRefsToLabels(tail)
                | SwitchTag(_, cases, defaultLabel) ->
                    let recursive addCases cs entries =
                        match cs with
                            | [] -> entries
                            | switchCase :: cTail ->
                                entries
                                |> bumpStr(switchCaseLabel(switchCase))
                                |> addCases(cTail)
                    in
                        acc
                        |> addCases(cases)
                        |> bumpStr(defaultLabel)
                        |> countBranchRefsToLabels(tail)
                | _ -> countBranchRefsToLabels(tail)(acc)

// How many branches target each label.
let branchRefsOf instructions = countBranchRefsToLabels(instructions)(Ashes.Collection.Map.empty)

let emptyFoldFacts =
    FoldFacts(
        ints = Ashes.Collection.Map.empty,
        floats = Ashes.Collection.Map.empty,
        bools = Ashes.Collection.Map.empty,
        localInts = Ashes.Collection.Map.empty,
        localFloats = Ashes.Collection.Map.empty,
        localBools = Ashes.Collection.Map.empty,
        touched = [],
        stamp = 0
    )

// The journal's table numbers.
let intsTable = 0

let floatsTable = 1

let boolsTable = 2

let localIntsTable = 3

let localFloatsTable = 4

let localBoolsTable = 5

let factsWithInt dest value (facts: FoldFacts) = facts with ints = optSet(dest)(value)(facts.ints), touched = (facts.stamp, intsTable, dest) :: facts.touched, stamp = facts.stamp + 1

let factsWithFloat dest value (facts: FoldFacts) = facts with floats = optSet(dest)(value)(facts.floats), touched = (facts.stamp, floatsTable, dest) :: facts.touched, stamp = facts.stamp + 1

let factsWithBool dest value (facts: FoldFacts) = facts with bools = optSet(dest)(value)(facts.bools), touched = (facts.stamp, boolsTable, dest) :: facts.touched, stamp = facts.stamp + 1

let factsWithLocalInt slot value (facts: FoldFacts) = facts with localInts = optSet(slot)(value)(facts.localInts), touched = (facts.stamp, localIntsTable, slot) :: facts.touched, stamp = facts.stamp + 1

let factsWithLocalFloat slot value (facts: FoldFacts) = facts with localFloats = optSet(slot)(value)(facts.localFloats), touched = (facts.stamp, localFloatsTable, slot) :: facts.touched, stamp = facts.stamp + 1

let factsWithLocalBool slot value (facts: FoldFacts) = facts with localBools = optSet(slot)(value)(facts.localBools), touched = (facts.stamp, localBoolsTable, slot) :: facts.touched, stamp = facts.stamp + 1

// Forgetting a key that holds nothing changes nothing and is not journaled.
let factsWithoutInt key (facts: FoldFacts) =
    match optGet(key)(facts.ints) with
        | Some(_) -> facts with ints = intSet(key)(None)(facts.ints), touched = (facts.stamp, intsTable, key) :: facts.touched, stamp = facts.stamp + 1
        | None -> facts

let factsWithoutFloat key (facts: FoldFacts) =
    match optGet(key)(facts.floats) with
        | Some(_) -> facts with floats = intSet(key)(None)(facts.floats), touched = (facts.stamp, floatsTable, key) :: facts.touched, stamp = facts.stamp + 1
        | None -> facts

let factsWithoutBool key (facts: FoldFacts) =
    match optGet(key)(facts.bools) with
        | Some(_) -> facts with bools = intSet(key)(None)(facts.bools), touched = (facts.stamp, boolsTable, key) :: facts.touched, stamp = facts.stamp + 1
        | None -> facts

let factsWithoutLocalInt slot (facts: FoldFacts) =
    match optGet(slot)(facts.localInts) with
        | Some(_) -> facts with localInts = intSet(slot)(None)(facts.localInts), touched = (facts.stamp, localIntsTable, slot) :: facts.touched, stamp = facts.stamp + 1
        | None -> facts

let factsWithoutLocalFloat slot (facts: FoldFacts) =
    match optGet(slot)(facts.localFloats) with
        | Some(_) -> facts with localFloats = intSet(slot)(None)(facts.localFloats), touched = (facts.stamp, localFloatsTable, slot) :: facts.touched, stamp = facts.stamp + 1
        | None -> facts

let factsWithoutLocalBool slot (facts: FoldFacts) =
    match optGet(slot)(facts.localBools) with
        | Some(_) -> facts with localBools = intSet(slot)(None)(facts.localBools), touched = (facts.stamp, localBoolsTable, slot) :: facts.touched, stamp = facts.stamp + 1
        | None -> facts

let factsWithStore slot source (facts: FoldFacts) =
    (let cleared =
        facts
        |> factsWithoutLocalInt(slot)
        |> factsWithoutLocalFloat(slot)
        |> factsWithoutLocalBool(slot)
    in
        match optGet(source)(facts.ints) with
            | Some(value) -> factsWithLocalInt(slot)(value)(cleared)
            | None ->
                match optGet(source)(facts.floats) with
                    | Some(value) -> factsWithLocalFloat(slot)(value)(cleared)
                    | None ->
                        match optGet(source)(facts.bools) with
                            | Some(value) -> factsWithLocalBool(slot)(value)(cleared)
                            | None -> cleared)

let intEquals (left: Int) (right: Int) = left == right

let floatEquals (left: Float) (right: Float) = left == right

let boolEquals (left: Bool) (right: Bool) = left == right

let recursive prependTouchedKeys (journal: List((Int, Int, Int))) (acc: List((Int, Int))) =
    match journal with
        | [] -> acc
        | (_, table, key) :: tail -> prependTouchedKeys(tail)((table, key) :: acc)

// Every (table, key) either fact set touched after the newest journal entry the two share. Both
// journals are newest first, so the larger stamp is always the one to step past.
let recursive divergentTouches (left: List((Int, Int, Int))) (right: List((Int, Int, Int))) (acc: List((Int, Int))) =
    match left with
        | [] -> prependTouchedKeys(right)(acc)
        | (leftStamp, leftTable, leftKey) :: leftTail ->
            match right with
                | [] -> prependTouchedKeys(left)(acc)
                | (rightStamp, rightTable, rightKey) :: rightTail ->
                    if leftStamp == rightStamp
                    then acc
                    else
                        if leftStamp > rightStamp
                        then divergentTouches(leftTail)(right)((leftTable, leftKey) :: acc)
                        else divergentTouches(left)(rightTail)((rightTable, rightKey) :: acc)

let agreesWith equalValues value other =
    match other with
        | Some(otherValue) -> equalValues(otherValue)(value)
        | None -> false

// An entry of `kept` survives only when the other edge carries the same key with the same value.
let reconcileTouchedKey (table: Int) (key: Int) (other: FoldFacts) (kept: FoldFacts) =
    if table == intsTable
    then
        match optGet(key)(kept.ints) with
            | None -> kept
            | Some(value) ->
                if other.ints
                |> optGet(key)
                |> agreesWith(intEquals)(value)
                then kept
                else factsWithoutInt(key)(kept)
    else
        if table == floatsTable
        then
            match optGet(key)(kept.floats) with
                | None -> kept
                | Some(value) ->
                    if other.floats
                    |> optGet(key)
                    |> agreesWith(floatEquals)(value)
                    then kept
                    else factsWithoutFloat(key)(kept)
        else
            if table == boolsTable
            then
                match optGet(key)(kept.bools) with
                    | None -> kept
                    | Some(value) ->
                        if other.bools
                        |> optGet(key)
                        |> agreesWith(boolEquals)(value)
                        then kept
                        else factsWithoutBool(key)(kept)
            else
                if table == localIntsTable
                then
                    match optGet(key)(kept.localInts) with
                        | None -> kept
                        | Some(value) ->
                            if other.localInts
                            |> optGet(key)
                            |> agreesWith(intEquals)(value)
                            then kept
                            else factsWithoutLocalInt(key)(kept)
                else
                    if table == localFloatsTable
                    then
                        match optGet(key)(kept.localFloats) with
                            | None -> kept
                            | Some(value) ->
                                if other.localFloats
                                |> optGet(key)
                                |> agreesWith(floatEquals)(value)
                                then kept
                                else factsWithoutLocalFloat(key)(kept)
                    else
                        match optGet(key)(kept.localBools) with
                            | None -> kept
                            | Some(value) ->
                                if other.localBools
                                |> optGet(key)
                                |> agreesWith(boolEquals)(value)
                                then kept
                                else factsWithoutLocalBool(key)(kept)

let recursive reconcileTouchedKeys (keys: List((Int, Int))) (other: FoldFacts) (kept: FoldFacts) =
    match keys with
        | [] -> kept
        | (table, key) :: rest ->
            kept
            |> reconcileTouchedKey(table)(key)(other)
            |> reconcileTouchedKeys(rest)(other)

// The meet over predecessor edges: an entry survives only when every other edge carries the same
// key with the same value. A key neither edge touched since their shared history holds the same
// thing on both, so only the keys touched since then are compared.
let recursive meetWithOthers (kept: FoldFacts) (others: List(FoldFacts)) =
    match others with
        | [] -> kept
        | other :: rest ->
            meetWithOthers(reconcileTouchedKeys(divergentTouches(kept.touched)(other.touched)([]))(other)(kept))(rest)

// `stamp` is the scan's next unused stamp, which the result carries on from.
let meetFacts (stamp: Int) (snapshots: List(FoldFacts)) =
    match snapshots with
        | [] -> emptyFoldFacts with stamp = stamp
        | first :: rest -> meetWithOthers((first with stamp = stamp))(rest)

// One snapshot per predecessor edge observed so far, keyed by target label.
let recordEdgeSnapshot (target: Str) facts saved =
    match strGet(target)(saved) with
        | Some(snapshots) ->
            strSet(target)(append(snapshots)([facts]))(saved)
        | None -> strSet(target)([facts])(saved)

// A label's snapshots are spent once the label is entered.
let forgetEdgeSnapshots (name: Str) saved =
    match strGet(name)(saved) with
        | Some(_ :: _) -> strSet(name)([])(saved)
        | _ -> saved

let recursive recordSwitchCaseSnapshots cases facts saved =
    match cases with
        | [] -> saved
        | switchCase :: tail ->
            saved
            |> recordEdgeSnapshot(switchCaseLabel(switchCase))(facts)
            |> recordSwitchCaseSnapshots(tail)(facts)

// The case a switch on an already-known tag takes: the first case carrying that tag, else the default.
let recursive switchTakenLabel tag cases defaultLabel =
    match cases with
        | [] -> defaultLabel
        | switchCase :: rest ->
            if switchCaseTag(switchCase) == tag
            then switchCaseLabel(switchCase)
            else switchTakenLabel(tag)(rest)(defaultLabel)

// The facts entering a label. When every predecessor edge (each explicit branch plus fall-through,
// if any) has been observed by this forward scan, the entering facts are the meet over those edges;
// a single-predecessor label and a fall-through-only label are the one-snapshot special case. A
// predecessor not yet observed (a backward branch, whose source appears later) is unknowable here,
// so a loop header conservatively clears every fact. Either way the facts carry on from the scan's
// current stamp.
let factsAtLabel (name: Str) (branchRefs: MapTree(Str, Int)) (facts: FoldFacts) (saved: MapTree(Str, List(FoldFacts))) (prevIsTerminator: Bool) =
    (let branchCount =
        match strGet(name)(branchRefs) with
            | Some(count) -> count
            | None -> 0
    in
        let fallthrough =
            if prevIsTerminator
            then 0
            else 1
        in
            let snapshots =
                match strGet(name)(saved) with
                    | Some(list) -> list
                    | None -> []
            in
                let total = branchCount + fallthrough
                in
                    let observed = length(snapshots) + fallthrough
                    in
                        if total > 0
                        then
                            if observed == total
                            then
                                meetFacts(facts.stamp)(
                                    if fallthrough == 1
                                    then facts :: snapshots
                                    else snapshots
                                )
                            else emptyFoldFacts with stamp = facts.stamp
                        else emptyFoldFacts with stamp = facts.stamp)

let foldIntPair (facts: FoldFacts) left right =
    match (optGet(left)(facts.ints), optGet(right)(facts.ints)) with
        | (Some(leftValue), Some(rightValue)) -> Some((leftValue, rightValue))
        | _ -> None

let foldFloatPair (facts: FoldFacts) left right =
    match (optGet(left)(facts.floats), optGet(right)(facts.floats)) with
        | (Some(leftValue), Some(rightValue)) -> Some((leftValue, rightValue))
        | _ -> None

let intResult dest value location = IrInstruction(instruction = LoadConstInt(dest)(value), location = location)

let floatResult dest value location = IrInstruction(instruction = LoadConstFloat(dest)(value), location = location)

let boolResult dest value location = IrInstruction(instruction = LoadConstBool(dest)(value), location = location)

let foldConstants instructions =
    (let branchRefs = branchRefsOf(instructions)
    in
        let recursive foldLoop insts (facts: FoldFacts) saved prevIsTerminator acc =
            match insts with
                | [] -> reverse(acc)
                | (IrInstruction { instruction = inst, location = loc } as irInst) :: tail ->
                    (match inst with
                        | LoadConstInt(dest, value) ->
                            foldLoop(tail)(factsWithInt(dest)(value)(facts))(saved)(false)(irInst :: acc)
                        | LoadConstFloat(dest, value) ->
                            foldLoop(tail)(factsWithFloat(dest)(value)(facts))(saved)(false)(irInst :: acc)
                        | LoadConstBool(dest, value) ->
                            foldLoop(tail)(factsWithBool(dest)(value)(facts))(saved)(false)(irInst :: acc)
                        | AddInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithInt(dest)(lv + rv)(facts))(saved)(false)(intResult(dest)(lv + rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | SubInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithInt(dest)(lv - rv)(facts))(saved)(false)(intResult(dest)(lv - rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | MulInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithInt(dest)(lv * rv)(facts))(saved)(false)(intResult(dest)(lv * rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | DivInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    if rv != 0
                                    then
                                        foldLoop(tail)(factsWithInt(dest)(lv / rv)(facts))(saved)(false)(intResult(dest)(lv / rv)(loc) :: acc)
                                    else foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | DivUInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    if rv != 0
                                    then
                                        foldLoop(tail)(factsWithInt(dest)(lv / rv)(facts))(saved)(false)(intResult(dest)(lv / rv)(loc) :: acc)
                                    else foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | AndInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithInt(dest)(lv & rv)(facts))(saved)(false)(intResult(dest)(lv & rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | OrInt(dest, l, r) ->
                            (match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    (foldLoop(tail)(factsWithInt(dest)(lv | rv)(facts))(saved)(false)(intResult(dest)(lv | rv)(loc) :: acc))
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc))
                        | XorInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithInt(dest)(lv ^ rv)(facts))(saved)(false)(intResult(dest)(lv ^ rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | ShlInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithInt(dest)(lv << rv)(facts))(saved)(false)(intResult(dest)(lv << rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | ShrInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithInt(dest)(lv >> rv)(facts))(saved)(false)(intResult(dest)(lv >> rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | AddFloat(dest, l, r) ->
                            match foldFloatPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithFloat(dest)(lv + rv)(facts))(saved)(false)(floatResult(dest)(lv + rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | SubFloat(dest, l, r) ->
                            match foldFloatPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithFloat(dest)(lv - rv)(facts))(saved)(false)(floatResult(dest)(lv - rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | MulFloat(dest, l, r) ->
                            match foldFloatPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithFloat(dest)(lv * rv)(facts))(saved)(false)(floatResult(dest)(lv * rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | DivFloat(dest, l, r) ->
                            match foldFloatPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithFloat(dest)(lv / rv)(facts))(saved)(false)(floatResult(dest)(lv / rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | CmpIntEq(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithBool(dest)(lv == rv)(facts))(saved)(false)(boolResult(dest)(lv == rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | CmpIntNe(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithBool(dest)(lv != rv)(facts))(saved)(false)(boolResult(dest)(lv != rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | CmpIntGt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithBool(dest)(lv > rv)(facts))(saved)(false)(boolResult(dest)(lv > rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | CmpIntGe(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithBool(dest)(lv >= rv)(facts))(saved)(false)(boolResult(dest)(lv >= rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | CmpIntLt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithBool(dest)(lv < rv)(facts))(saved)(false)(boolResult(dest)(lv < rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | CmpIntLe(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    foldLoop(tail)(factsWithBool(dest)(lv <= rv)(facts))(saved)(false)(boolResult(dest)(lv <= rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | StoreLocal(slot, source) ->
                            foldLoop(tail)(factsWithStore(slot)(source)(facts))(saved)(false)(irInst :: acc)
                        | LoadLocal(dest, slot) ->
                            match optGet(slot)(facts.localInts) with
                                | Some(value) ->
                                    foldLoop(tail)(factsWithInt(dest)(value)(facts))(saved)(false)(intResult(dest)(value)(loc) :: acc)
                                | None ->
                                    match optGet(slot)(facts.localFloats) with
                                        | Some(value) ->
                                            foldLoop(tail)(factsWithFloat(dest)(value)(facts))(saved)(false)(floatResult(dest)(value)(loc) :: acc)
                                        | None ->
                                            match optGet(slot)(facts.localBools) with
                                                | Some(value) ->
                                                    foldLoop(tail)(factsWithBool(dest)(value)(facts))(saved)(false)(boolResult(dest)(value)(loc) :: acc)
                                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | Label(name) ->
                            foldLoop(tail)(factsAtLabel(name)(branchRefs)(facts)(saved)(prevIsTerminator))(forgetEdgeSnapshots(name)(saved))(false)(irInst :: acc)
                        | Jump(target) ->
                            foldLoop(tail)(facts)(recordEdgeSnapshot(target)(facts)(saved))(true)(irInst :: acc)
                        | JumpIfFalse(condition, target) ->
                            match optGet(condition)(facts.bools) with
                                | Some(true) -> foldLoop(tail)(facts)(saved)(false)(acc)
                                | Some(false) ->
                                    foldLoop(tail)(facts)(recordEdgeSnapshot(target)(facts)(saved))(true)(IrInstruction(instruction = Jump(target), location = loc) :: acc)
                                | None ->
                                    foldLoop(tail)(facts)(recordEdgeSnapshot(target)(facts)(saved))(false)(irInst :: acc)
                        | SwitchTag(tagTemp, cases, defaultLabel) ->
                            match optGet(tagTemp)(facts.ints) with
                                | Some(tag) ->
                                    foldLoop(tail)(facts)(recordEdgeSnapshot(switchTakenLabel(tag)(cases)(defaultLabel))(facts)(saved))(true)(IrInstruction(instruction = defaultLabel
                                    |> switchTakenLabel(tag)(cases)
                                    |> Jump, location = loc) :: acc)
                                | None ->
                                    foldLoop(tail)(facts)(saved
                                    |> recordSwitchCaseSnapshots(cases)(facts)
                                    |> recordEdgeSnapshot(defaultLabel)(facts))(true)(irInst :: acc)
                        | Return(_) -> foldLoop(tail)(facts)(saved)(true)(irInst :: acc)
                        | _ -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc))
        in foldLoop(instructions)(emptyFoldFacts)(Ashes.Collection.Map.empty)(false)([]))

// The facts saved for a label are spent once the label is entered.
let forgetSavedInts (name: Str) saved =
    match strGet(name)(saved) with
        | Some(_) -> strSet(name)(Ashes.Collection.Map.empty)(saved)
        | None -> saved

let reduceIdentitiesAndStrength instructions =
    (let branchRefs = branchRefsOf(instructions)
    in
        let recursive reduceLoop insts knownInts savedInts prevTerm acc =
            match insts with
                | [] -> reverse(acc)
                | (IrInstruction { instruction = inst, location = loc } as irInst) :: tail ->
                    match inst with
                        | LoadConstInt(dest, v) ->
                            let nextInts = intSet(dest)(v)(knownInts)
                            in reduceLoop(tail)(nextInts)(savedInts)(false)(irInst :: acc)
                        | AddInt(dest, l, r) ->
                            let leftZero = intGet(l)(knownInts) == Some(0)
                            in
                                let rightZero = intGet(r)(knownInts) == Some(0)
                                in
                                    if leftZero
                                    then
                                        reduceLoop(tail)(knownInts)(savedInts)(false)(
                                            IrInstruction(instruction = Borrow(dest)(r), location = loc) :: acc
                                        )
                                    else
                                        if rightZero
                                        then
                                            reduceLoop(tail)(knownInts)(savedInts)(false)(
                                                IrInstruction(instruction = Borrow(dest)(l), location = loc) :: acc
                                            )
                                        else reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
                        | SubInt(dest, l, r) ->
                            let rightZero = intGet(r)(knownInts) == Some(0)
                            in
                                if rightZero
                                then
                                    reduceLoop(tail)(knownInts)(savedInts)(false)(
                                        IrInstruction(instruction = Borrow(dest)(l), location = loc) :: acc
                                    )
                                else reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
                        | MulInt(dest, l, r) ->
                            let lOpt = intGet(l)(knownInts)
                            in
                                let rOpt = intGet(r)(knownInts)
                                in
                                    let isZero =
                                        if lOpt == Some(0)
                                        then true
                                        else rOpt == Some(0)
                                    in
                                        if isZero
                                        then
                                            reduceLoop(tail)(knownInts)(savedInts)(false)(
                                                IrInstruction(instruction = LoadConstInt(dest)(0), location = loc) :: acc
                                            )
                                        else
                                            if lOpt == Some(1)
                                            then
                                                reduceLoop(tail)(knownInts)(savedInts)(false)(
                                                    IrInstruction(instruction = Borrow(dest)(r), location = loc) :: acc
                                                )
                                            else
                                                if rOpt == Some(1)
                                                then
                                                    reduceLoop(tail)(knownInts)(savedInts)(false)(
                                                        IrInstruction(instruction = Borrow(dest)(l), location = loc) :: acc
                                                    )
                                                else
                                                    if rOpt == Some(2)
                                                    then
                                                        reduceLoop(tail)(knownInts)(savedInts)(false)(
                                                            IrInstruction(
                                                                instruction = AddInt(dest)(l)(l),
                                                                location = loc
                                                            ) :: acc
                                                        )
                                                    else
                                                        if lOpt == Some(2)
                                                        then
                                                            reduceLoop(tail)(knownInts)(savedInts)(false)(
                                                                IrInstruction(
                                                                    instruction = AddInt(dest)(r)(r),
                                                                    location = loc
                                                                ) :: acc
                                                            )
                                                        else reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
                        | DivInt(dest, l, r) ->
                            let rightOne = intGet(r)(knownInts) == Some(1)
                            in
                                if rightOne
                                then
                                    reduceLoop(tail)(knownInts)(savedInts)(false)(
                                        IrInstruction(instruction = Borrow(dest)(l), location = loc) :: acc
                                    )
                                else reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
                        | DivUInt(dest, l, r) ->
                            let rightOne = intGet(r)(knownInts) == Some(1)
                            in
                                if rightOne
                                then
                                    reduceLoop(tail)(knownInts)(savedInts)(false)(
                                        IrInstruction(instruction = Borrow(dest)(l), location = loc) :: acc
                                    )
                                else reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
                        | Label(name) ->
                            let branchCount =
                                match strGet(name)(branchRefs) with
                                    | Some(c) -> c
                                    | None -> 0
                            in
                                let totalPreds =
                                    branchCount + (if prevTerm
                                    then 0
                                    else 1)
                                in
                                    let nextInts =
                                        if totalPreds <= 1
                                        then
                                            if prevTerm
                                            then
                                                match strGet(name)(savedInts) with
                                                    | Some(si) -> si
                                                    | None -> Ashes.Collection.Map.empty
                                            else
                                                if branchCount == 0
                                                then knownInts
                                                else Ashes.Collection.Map.empty
                                        else Ashes.Collection.Map.empty
                                    in
                                        reduceLoop(tail)(nextInts)(forgetSavedInts(name)(savedInts))(false)(
                                            irInst :: acc
                                        )
                        | Jump(target) ->
                            let nextSaved = strSet(target)(knownInts)(savedInts)
                            in reduceLoop(tail)(knownInts)(nextSaved)(true)(irInst :: acc)
                        | JumpIfFalse(_, target) ->
                            let nextSaved = strSet(target)(knownInts)(savedInts)
                            in reduceLoop(tail)(knownInts)(nextSaved)(false)(irInst :: acc)
                        | SwitchTag(_, _, _) -> reduceLoop(tail)(knownInts)(savedInts)(true)(irInst :: acc)
                        | Return(_) -> reduceLoop(tail)(knownInts)(savedInts)(true)(irInst :: acc)
                        | _ -> reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
        in reduceLoop(instructions)(Ashes.Collection.Map.empty)(Ashes.Collection.Map.empty)(false)([]))

// Predecessor counts are rebuilt from this pass's own input rather than shared with the folding
// pass: branch folding above can remove the only edge that used to target a label, so a label with
// zero remaining branch references that sits inside an unreachable region cannot be entered at all
// and is dropped together with its body, instead of surviving as emitted-but-dead code.
let elideUnreachableCode instructions =
    (let branchRefs = branchRefsOf(instructions)
    in
        let recursive elideLoop insts unreachable acc =
            match insts with
                | [] -> reverse(acc)
                | (IrInstruction { instruction = inst } as irInst) :: tail ->
                    match inst with
                        | Label(name) ->
                            let referenced =
                                match strGet(name)(branchRefs) with
                                    | Some(count) -> count > 0
                                    | None -> false
                            in
                                if unreachable
                                then
                                    if referenced
                                    then elideLoop(tail)(false)(irInst :: acc)
                                    else elideLoop(tail)(true)(acc)
                                else elideLoop(tail)(false)(irInst :: acc)
                        | _ ->
                            if unreachable
                            then elideLoop(tail)(true)(acc)
                            else
                                let isTerm =
                                    match inst with
                                        | Jump(_) -> true
                                        | Return(_) -> true
                                        | SwitchTag(_, _, _) -> true
                                        | _ -> false
                                in elideLoop(tail)(isTerm)(irInst :: acc)
        in elideLoop(instructions)(false)([]))

let recursive collectAllUsedTemps instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            acc
            |> addAllToSet(getUsedTemps(inst))
            |> collectAllUsedTemps(tail)

let recursive collectAllReadSlots instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            acc
            |> addAllToSet(getReadLocalSlots(inst))
            |> collectAllReadSlots(tail)

let isDeadInstruction inst usedTemps readSlots =
    match inst with
        | LoadConstInt(t, _) -> !intHas(t)(usedTemps)
        | LoadConstFloat(t, _) -> !intHas(t)(usedTemps)
        | LoadConstBool(t, _) -> !intHas(t)(usedTemps)
        | StoreLocal(slot, _) -> !intHas(slot)(readSlots)
        | MakeClosure(t, _, _, _, _, _, _, _) -> !intHas(t)(usedTemps)
        | MakeClosureStack(t, _, _, _, _, _, _) -> !intHas(t)(usedTemps)
        | _ -> false

let elideDeadCode instructions =
    (let recursive fixpoint insts =
        let usedTemps = collectAllUsedTemps(insts)(Ashes.Collection.Map.empty)
        in
            let readSlots = collectAllReadSlots(insts)(Ashes.Collection.Map.empty)
            in
                let filtered =
                    filter(
                        given (irInst) ->
                            match irInst with
                                | IrInstruction { instruction = inst } -> !isDeadInstruction(inst)(usedTemps)(readSlots)
                    )(
                        insts
                    )
                in
                    if length(filtered) == length(insts)
                    then insts
                    else fixpoint(filtered)
    in fixpoint(instructions))

let recursive hasErasedRcDrop instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = RcDrop(_, _, _, false, _, _) } :: _ -> true
        | _ :: tail -> hasErasedRcDrop(tail)

// A body with no erased RcDrop is returned as it is.
let elideErasedRcDrops instructions =
    if hasErasedRcDrop(instructions)
    then
        let recursive collectLoadLocals insts (idx: Int) acc =
            match insts with
                | [] -> acc
                | IrInstruction { instruction = LoadLocal(t, slot) } :: tail ->
                    acc
                    |> intSet(t)(idx)
                    |> collectLoadLocals(tail)(idx + 1)
                | _ :: tail -> collectLoadLocals(tail)(idx + 1)(acc)
        in
            let useCounts = useCountsOf(instructions)
            in
                let loadLocalDefs = collectLoadLocals(instructions)(0)(Ashes.Collection.Map.empty)
                in
                    let recursive findElidableDrops insts (idx: Int) acc =
                        match insts with
                            | [] -> acc
                            | IrInstruction { instruction = RcDrop(src, _, _, false, _, _) } :: tail ->
                                let accWithDrop = intSet(idx)(true)(acc)
                                in
                                    let accWithLoad =
                                        match intGet(src)(loadLocalDefs) with
                                            | Some(defIdx) ->
                                                if intGet(src)(useCounts) == Some(1)
                                                then intSet(defIdx)(true)(accWithDrop)
                                                else accWithDrop
                                            | None -> accWithDrop
                                    in findElidableDrops(tail)(idx + 1)(accWithLoad)
                            | _ :: tail -> findElidableDrops(tail)(idx + 1)(acc)
                    in
                        let toRemove = findElidableDrops(instructions)(0)(Ashes.Collection.Map.empty)
                        in
                            let recursive filterInsts insts (idx: Int) acc =
                                match insts with
                                    | [] -> reverse(acc)
                                    | head :: tail ->
                                        if intHas(idx)(toRemove)
                                        then filterInsts(tail)(idx + 1)(acc)
                                        else filterInsts(tail)(idx + 1)(head :: acc)
                            in filterInsts(instructions)(0)([])
    else instructions

let isNonAllocatingInst nonAllocatingFns inst =
    match inst with
        | LoadConstInt(_, _) -> true
        | LoadConstFloat(_, _) -> true
        | LoadConstBool(_, _) -> true
        | LoadConstStr(_, _) -> true
        | LoadLocal(_, _) -> true
        | StoreLocal(_, _) -> true
        | LoadEnv(_, _) -> true
        | LoadMemOffset(_, _, _) -> true
        | StoreMemOffset(_, _, _) -> true
        | AddInt(_, _, _) -> true
        | SubInt(_, _, _) -> true
        | MulInt(_, _, _) -> true
        | DivInt(_, _, _) -> true
        | DivUInt(_, _, _) -> true
        | AndInt(_, _, _) -> true
        | OrInt(_, _, _) -> true
        | XorInt(_, _, _) -> true
        | ShlInt(_, _, _) -> true
        | ShrInt(_, _, _) -> true
        | AddFloat(_, _, _) -> true
        | SubFloat(_, _, _) -> true
        | MulFloat(_, _, _) -> true
        | DivFloat(_, _, _) -> true
        | IntToFloat(_, _) -> true
        | FloatToInt(_, _) -> true
        | FloatUnaryIntrinsic(_, _, _) -> true
        | CallLibm(_, _, _) -> true
        | CmpIntGt(_, _, _) -> true
        | CmpIntGe(_, _, _) -> true
        | CmpIntLt(_, _, _) -> true
        | CmpIntLe(_, _, _) -> true
        | CmpUIntGt(_, _, _) -> true
        | CmpUIntGe(_, _, _) -> true
        | CmpUIntLt(_, _, _) -> true
        | CmpUIntLe(_, _, _) -> true
        | CmpIntEq(_, _, _) -> true
        | CmpIntNe(_, _, _) -> true
        | CmpFloatGt(_, _, _) -> true
        | CmpFloatGe(_, _, _) -> true
        | CmpFloatLt(_, _, _) -> true
        | CmpFloatLe(_, _, _) -> true
        | CmpFloatEq(_, _, _) -> true
        | CmpFloatNe(_, _, _) -> true
        | CmpStrEq(_, _, _) -> true
        | CmpStrNe(_, _, _) -> true
        | LoadFuncAddr(_, _) -> true
        | GetAdtTag(_, _) -> true
        | LoadArgumentOwnership(_) -> true
        | GetAdtField(_, _, _, _) -> true
        | SetAdtField(_, _, _, _) -> true
        | Borrow(_, _) -> true
        | DropReuse(_, _, _, _) -> true
        | RcDup(_, _, _, _) -> true
        | RcDrop(_, _, _, _, _, _) -> true
        | RcIsUnique(_, _) -> true
        | IsReferenceCounted(_, _) -> true
        | BytesLength(_, _) -> true
        | BytesGet(_, _, _) -> true
        | BytesCompare(_, _, _) -> true
        | BytesIndexOf(_, _, _, _) -> true
        | BytesHash(_, _) -> true
        | BytesGetU16Le(_, _, _) -> true
        | BytesGetU32Le(_, _, _) -> true
        | BytesGetU64Le(_, _, _) -> true
        | TextByteLength(_, _) -> true
        | SaveArenaState(_, _, _) -> true
        | RestoreArenaState(_, _, _, _) -> true
        | ReclaimArenaChunks(_, _, _) -> true
        | SaveStackPointer(_) -> true
        | RestoreStackPointer(_) -> true
        | Label(_) -> true
        | Jump(_) -> true
        | JumpIfFalse(_, _) -> true
        | SwitchTag(_, _, _) -> true
        | Return(_) -> true
        | CallKnown(_, label, _, _, _, _) -> strHas(label)(nonAllocatingFns)
        | _ -> false

let recursive allInstructionsNonAllocating nonAllocatingFns instructions =
    match instructions with
        | [] -> true
        | IrInstruction { instruction = inst } :: tail ->
            if isNonAllocatingInst(nonAllocatingFns)(inst)
            then allInstructionsNonAllocating(nonAllocatingFns)(tail)
            else false

let functionLabel (fn: IrFunction) = fn.label

let functionInstructions (fn: IrFunction) = fn.instructions

// Every function by label; the first function carrying a label is the one a lookup finds.
let recursive functionsByLabel (functions: List(IrFunction)) (acc: MapTree(Str, IrFunction)) =
    match functions with
        | [] -> acc
        | (IrFunction { label = label } as fn) :: tail ->
            if strHas(label)(acc)
            then functionsByLabel(tail)(acc)
            else
                acc
                |> Ashes.Collection.Map.setStr(label)(fn)
                |> functionsByLabel(tail)

let recursive bumpWanted (keys: List(Int)) wanted counts =
    match keys with
        | [] -> counts
        | key :: rest ->
            if intHas(key)(wanted)
            then
                counts
                |> bumpInt(key)
                |> bumpWanted(rest)(wanted)
            else bumpWanted(rest)(wanted)(counts)

// How often each wanted temp is defined.
let recursive defCountsOfWanted instructions wanted counts =
    match instructions with
        | [] -> counts
        | IrInstruction { instruction = inst } :: tail ->
            counts
            |> bumpWanted(getDefinedTemps(inst))(wanted)
            |> defCountsOfWanted(tail)(wanted)

// The defining instruction of each wanted temp that has exactly one definition: a pass that only
// ever asks about a few temps does not pay for a table of all of them. `defCounts` holds the
// wanted temps alone, so any temp it counts once is a wanted one.
let singleDefinitionsOfWanted instructions wanted =
    Ashes.Collection.Map.empty
    |> defCountsOfWanted(instructions)(wanted)
    |> singleDefsOf(instructions)

// How often each wanted temp is used.
let recursive useCountsOfWanted instructions wanted counts =
    match instructions with
        | [] -> counts
        | IrInstruction { instruction = inst } :: tail ->
            counts
            |> bumpWanted(getUsedTemps(inst))(wanted)
            |> useCountsOfWanted(tail)(wanted)

let recursive collectReturnedTemps instructions (acc: List(IrTemp)) =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = Return(source) } :: tail -> collectReturnedTemps(tail)(source :: acc)
        | _ :: tail -> collectReturnedTemps(tail)(acc)

// The heap closure label a temp provably holds comes directly from a MakeClosure (never a
// MakeClosureStack, whose environment lives in its defining function's own frame and is gone once
// that function returns), or transitively through a CallKnown to a function already proven to
// return one label. This is the defining instruction of every Return's source, or None as soon as
// one of them is neither.
let recursive returnedDefinitionsOf (returned: List(IrTemp)) singleDefs (acc: List(IrInstructionKind)) =
    match returned with
        | [] -> Some(acc)
        | source :: rest ->
            match intGet(source)(singleDefs) with
                | Some(definition) ->
                    match definition with
                        | MakeClosure(_, _, _, _, _, _, _, _) -> returnedDefinitionsOf(rest)(singleDefs)(definition :: acc)
                        | CallKnown(_, _, _, _, _, _) -> returnedDefinitionsOf(rest)(singleDefs)(definition :: acc)
                        | _ -> None
                | None -> None

// One (label, returned definitions) summary per function whose every Return could still yield a
// known closure, computed once so the fixpoint rounds never look at a function body again.
let recursive summarizeReturns (functions: List(IrFunction)) (acc: List((Str, List(IrInstructionKind)))) =
    match functions with
        | [] -> acc
        | fn :: rest ->
            match collectReturnedTemps(fn.instructions)([]) with
                | [] -> summarizeReturns(rest)(acc)
                | returned ->
                    let singleDefs =
                        Ashes.Collection.Map.empty
                        |> addAllToSet(returned)
                        |> singleDefinitionsOfWanted(fn.instructions)
                    in
                        match returnedDefinitionsOf(returned)(singleDefs)([]) with
                            | Some(definitions) -> summarizeReturns(rest)((fn.label, definitions) :: acc)
                            | None -> summarizeReturns(rest)(acc)

let returnedLabelOf definition knownLabels =
    match definition with
        | MakeClosure(_, label, _, _, _, _, _, _) -> Some(label)
        | CallKnown(_, callee, _, _, _, _) -> strGet(callee)(knownLabels)
        | _ -> None

// The one heap closure label every Return of the function provably yields, if any.
let recursive resolveReturnedLabel (values: List(IrInstructionKind)) knownLabels (current: Maybe(Str)) =
    match values with
        | [] -> current
        | value :: rest ->
            match returnedLabelOf(value)(knownLabels) with
                | None -> None
                | Some(label) ->
                    match current with
                        | None -> resolveReturnedLabel(rest)(knownLabels)(Some(label))
                        | Some(previous) ->
                            if previous == label
                            then resolveReturnedLabel(rest)(knownLabels)(current)
                            else None

let recursive addKnownReturnedClosureLabels (summaries: List((Str, List(IrInstructionKind)))) knownLabels (pending: List((Str, List(IrInstructionKind)))) (changed: Bool) =
    match summaries with
        | [] -> (knownLabels, pending, changed)
        | summary :: rest ->
            match summary with
                | (label, values) ->
                    if strHas(label)(knownLabels)
                    then addKnownReturnedClosureLabels(rest)(knownLabels)(pending)(changed)
                    else
                        match resolveReturnedLabel(values)(knownLabels)(None) with
                            | Some(resolved) ->
                                addKnownReturnedClosureLabels(rest)(strSet(label)(resolved)(knownLabels))(pending)(true)
                            | None -> addKnownReturnedClosureLabels(rest)(knownLabels)(summary :: pending)(changed)

// Every round visits the summaries in program order, so when two functions carry the same label
// the first one that resolves decides it.
let recursive runKnownReturnedClosureLabels (summaries: List((Str, List(IrInstructionKind)))) knownLabels =
    match addKnownReturnedClosureLabels(summaries)(knownLabels)([])(false) with
        | (nextLabels, pending, true) ->
            runKnownReturnedClosureLabels(reverse(pending))(nextLabels)
        | (nextLabels, _, false) -> nextLabels

// Whole-program least fixpoint: each pass admits the functions whose returns resolve against the
// labels known so far, so a chain of curried helpers converges over several passes while a
// genuine cycle never does. Only the functions still unresolved are revisited.
let computeKnownReturnedClosureLabels (functions: List(IrFunction)) =
    runKnownReturnedClosureLabels([]
    |> summarizeReturns(functions)
    |> reverse)(Ashes.Collection.Map.empty)

// Every single-defined temp of the function mapped to its defining instruction.
let functionSingleDefinitions (fn: IrFunction) =
    fn.instructions
    |> defCountsOf
    |> singleDefsOf(fn.instructions)

let recursive containsCallClosure instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CallClosure(_, _, _, _) } :: _ -> true
        | _ :: tail -> containsCallClosure(tail)

let recursive collectClosureCallTemps instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = CallClosure(_, closureTemp, _, _) } :: tail ->
            acc
            |> intSet(closureTemp)(true)
            |> collectClosureCallTemps(tail)
        | _ :: tail -> collectClosureCallTemps(tail)(acc)

// A CallClosure whose closure temp is the result of a CallKnown to a function with a known
// returned label becomes a plain read of the closure object's environment word (offset 8, the
// layout the ordinary closure call reads) followed by a direct CallKnown of that label. The read
// neither consumes nor extends the closure object's lifetime, so drop placement is undisturbed.
let recursive devirtualizeReturnedClosureCallsOnce insts singleDefs knownLabels nextTemp acc changed =
    match insts with
        | [] -> (reverse(acc), nextTemp, changed)
        | (IrInstruction { instruction = CallClosure(dest, closureTemp, argTemp, flagTemp), location = loc } as irInst) :: tail ->
            match intGet(closureTemp)(singleDefs) with
                | Some(CallKnown(_, calleeLabel, _, _, _, _)) ->
                    match strGet(calleeLabel)(knownLabels) with
                        | Some(label) ->
                            devirtualizeReturnedClosureCallsOnce(tail)(singleDefs)(knownLabels)(nextTemp + 1)(
                                IrInstruction(instruction = CallKnown(dest)(label)(nextTemp)(argTemp)(flagTemp)(false), location = loc) :: IrInstruction(instruction = LoadMemOffset(nextTemp)(closureTemp)(8), location = loc) :: acc
                            )(
                                true
                            )
                        | None -> devirtualizeReturnedClosureCallsOnce(tail)(singleDefs)(knownLabels)(nextTemp)(irInst :: acc)(changed)
                | _ -> devirtualizeReturnedClosureCallsOnce(tail)(singleDefs)(knownLabels)(nextTemp)(irInst :: acc)(changed)
        | head :: tail -> devirtualizeReturnedClosureCallsOnce(tail)(singleDefs)(knownLabels)(nextTemp)(head :: acc)(changed)

// Whether a rewrite is possible at all: at least one known label and a CallClosure to resolve.
let mayResolveReturnedClosureCalls knownLabels instructions =
    if Ashes.Collection.Map.isEmpty(knownLabels)
    then false
    else containsCallClosure(instructions)

// Only the closure temps' definitions are tabled.
let closureCallDefinitions instructions =
    Ashes.Collection.Map.empty
    |> collectClosureCallTemps(instructions)
    |> singleDefinitionsOfWanted(instructions)

// Iterates to the function's own local fixed point so a curry deeper than two arguments resolves
// fully in one optimization: each rewrite turns a CallClosure into a CallKnown that the next
// CallClosure in the chain can resolve through.
let recursive devirtualizeReturnedClosureCallsInFunction knownLabels (fn: IrFunction) =
    if mayResolveReturnedClosureCalls(knownLabels)(fn.instructions)
    then
        match devirtualizeReturnedClosureCallsOnce(fn.instructions)(closureCallDefinitions(fn.instructions))(knownLabels)(fn.tempCount)([])(false) with
            | (_, _, false) -> fn
            | (rewritten, nextTemp, true) -> devirtualizeReturnedClosureCallsInFunction(knownLabels)((fn with instructions = rewritten, tempCount = nextTemp))
    else fn

let recursive keepNonAllocating (candidates: MapTree(Str, IrFunction)) (functions: List(IrFunction)) (acc: List(IrFunction)) =
    match functions with
        | [] -> reverse(acc)
        | fn :: tail ->
            if allInstructionsNonAllocating(candidates)(fn.instructions)
            then keepNonAllocating(candidates)(tail)(fn :: acc)
            else keepNonAllocating(candidates)(tail)(acc)

// The first function carrying each label, in program order.
let recursive firstOfEachLabel (functions: List(IrFunction)) (seen: MapTree(Str, Bool)) (acc: List(IrFunction)) =
    match functions with
        | [] -> reverse(acc)
        | (IrFunction { label = label } as fn) :: tail ->
            if strHas(label)(seen)
            then firstOfEachLabel(tail)(seen)(acc)
            else
                firstOfEachLabel(tail)(strSet(label)(true)(seen))(fn :: acc)

// The non-allocating functions, by label: the largest set whose members only call members. A
// label shared by several functions is decided by the first of them alone.
let computeNonAllocatingFunctions (functions: List(IrFunction)) =
    (let recursive fixpoint (candidates: List(IrFunction)) =
        let byLabel = functionsByLabel(candidates)(Ashes.Collection.Map.empty)
        in
            let kept = keepNonAllocating(byLabel)(candidates)([])
            in
                if length(kept) == length(candidates)
                then byLabel
                else fixpoint(kept)
    in
        []
        |> firstOfEachLabel(functions)(Ashes.Collection.Map.empty)
        |> fixpoint)

// Every reclaim's (pre slot, position) by the end slot it reads, latest position first.
let recursive indexBracketReclaims insts (position: Int) (acc: MapTree(Int, List((Int, Int)))) =
    match insts with
        | [] -> acc
        | IrInstruction { instruction = ReclaimArenaChunks(recEnd, recPre, _) } :: tail ->
            let known =
                match intGet(recEnd)(acc) with
                    | Some(entries) -> entries
                    | None -> []
            in
                acc
                |> intSet(recEnd)((recPre, position) :: known)
                |> indexBracketReclaims(tail)(position + 1)
        | _ :: tail -> indexBracketReclaims(tail)(position + 1)(acc)

let recursive earliestReclaimFrom (from: Int) (preSlot: Int) (entries: List((Int, Int))) (best: List(Int)) =
    match entries with
        | [] -> best
        | (pre, position) :: tail ->
            if position < from
            then best
            else
                if pre == preSlot
                then earliestReclaimFrom(from)(preSlot)(tail)([position])
                else earliestReclaimFrom(from)(preSlot)(tail)(best)

// The index of the bracket's reclaim, which reads the slots the bracket's save and restore
// wrote: right after the restore, or past the conditional copy-out block a call window places
// between the two. Absent when the bracket closes without one.
let findBracketReclaim reclaims (from: Int) (endSlot: Int) (preSlot: Int) =
    match intGet(endSlot)(reclaims) with
        | Some(entries) -> earliestReclaimFrom(from)(preSlot)(entries)([])
        | None -> []

let recursive withoutArenaBrackets instructions acc =
    match instructions with
        | [] -> reverse(acc)
        | IrInstruction { instruction = SaveArenaState(_, _, _) } :: tail -> withoutArenaBrackets(tail)(acc)
        | IrInstruction { instruction = RestoreArenaState(_, _, _, _) } :: tail -> withoutArenaBrackets(tail)(acc)
        | IrInstruction { instruction = ReclaimArenaChunks(_, _, _) } :: tail -> withoutArenaBrackets(tail)(acc)
        | head :: tail -> withoutArenaBrackets(tail)(head :: acc)

let stripRedundantArenaBrackets nonAllocatingFns (fn: IrFunction) =
    (let isWholeNonAllocating =
        if strHas(fn.label)(nonAllocatingFns)
        then true
        else allInstructionsNonAllocating(nonAllocatingFns)(fn.instructions)
    in
        if isWholeNonAllocating
        then
            let stripped = withoutArenaBrackets(fn.instructions)([])
            in
                IrFunction(
                    label = fn.label,
                    instructions = stripped,
                    localCount = fn.localCount,
                    tempCount = fn.tempCount,
                    hasEnvAndArgParams = fn.hasEnvAndArgParams,
                    coroutine = fn.coroutine,
                    localNames = fn.localNames,
                    localTypes = fn.localTypes,
                    origin = fn.origin,
                    lifetimesPlaced = fn.lifetimesPlaced
                )
        else
            let reclaims = indexBracketReclaims(fn.instructions)(0)(Ashes.Collection.Map.empty)
            in
                let recursive scanStraightLine insts (idx: Int) toRemove =
                    match insts with
                        | [] -> toRemove
                        | IrInstruction { instruction = SaveArenaState(curSlot, endSlot, _) } :: tail ->
                            let recursive findMatchingRestore rInsts (j: Int) =
                                match rInsts with
                                    | [] -> None
                                    | IrInstruction { instruction = RestoreArenaState(rCur, rEnd, preSlot, _) } :: rTail ->
                                        if rCur == curSlot
                                        then
                                            if rEnd == endSlot
                                            then Some(j :: findBracketReclaim(reclaims)(j + 1)(endSlot)(preSlot))
                                            else None
                                        else None
                                    | IrInstruction { instruction = inst } :: rTail ->
                                        match inst with
                                            | Label(_) -> None
                                            | Jump(_) -> None
                                            | JumpIfFalse(_, _) -> None
                                            | SwitchTag(_, _, _) -> None
                                            | _ ->
                                                if isNonAllocatingInst(nonAllocatingFns)(inst)
                                                then findMatchingRestore(rTail)(j + 1)
                                                else None
                            in
                                match findMatchingRestore(tail)(idx + 1) with
                                    | Some(foundIndices) ->
                                        toRemove
                                        |> intSet(idx)(true)
                                        |> addAllToSet(foundIndices)
                                        |> scanStraightLine(tail)(idx + 1)
                                    | None -> scanStraightLine(tail)(idx + 1)(toRemove)
                        | _ :: tail -> scanStraightLine(tail)(idx + 1)(toRemove)
                in
                    let removals = scanStraightLine(fn.instructions)(0)(Ashes.Collection.Map.empty)
                    in
                        if Ashes.Collection.Map.isEmpty(removals)
                        then fn
                        else
                            let recursive filterRemovals insts (idx: Int) acc =
                                match insts with
                                    | [] -> reverse(acc)
                                    | head :: tail ->
                                        if intHas(idx)(removals)
                                        then filterRemovals(tail)(idx + 1)(acc)
                                        else filterRemovals(tail)(idx + 1)(head :: acc)
                            in
                                let filtered = filterRemovals(fn.instructions)(0)([])
                                in
                                    IrFunction(
                                        label = fn.label,
                                        instructions = filtered,
                                        localCount = fn.localCount,
                                        tempCount = fn.tempCount,
                                        hasEnvAndArgParams = fn.hasEnvAndArgParams,
                                        coroutine = fn.coroutine,
                                        localNames = fn.localNames,
                                        localTypes = fn.localTypes,
                                        origin = fn.origin,
                                        lifetimesPlaced = fn.lifetimesPlaced
                                    ))

// Local common-subexpression elimination, scoped to a single straight-line block (reset at every
// label, never across control flow). Operands are canonicalized through a
// LoadLocal/StoreLocal/Borrow/RcDup alias map before keying the caches: real Ashes IR round-trips
// almost every value through a local slot, so raw-temp-identity keying would fold nothing (the
// ubiquitous `let x = p.x in let y = p.x` shape never matches without it).
// Deny by default: only these instruction kinds are known never to write through an existing
// pointer or call into unmodeled effects. Arena and stack bookkeeping qualifies because it moves
// an allocator cursor rather than writing through a pointer, and every `let` binding gets its own
// such bracket in practice, so treating it as aliasing would silence the pass almost everywhere.
let isLocalCseSafeInstruction inst =
    match inst with
        | LoadConstInt(_, _) -> true
        | LoadConstFloat(_, _) -> true
        | LoadConstBool(_, _) -> true
        | LoadConstStr(_, _) -> true
        | LoadLocal(_, _) -> true
        | StoreLocal(_, _) -> true
        | RcDup(_, _, _, _) -> true
        | Borrow(_, _) -> true
        | AddInt(_, _, _) -> true
        | SubInt(_, _, _) -> true
        | MulInt(_, _, _) -> true
        | DivInt(_, _, _) -> true
        | DivUInt(_, _, _) -> true
        | AndInt(_, _, _) -> true
        | OrInt(_, _, _) -> true
        | XorInt(_, _, _) -> true
        | ShlInt(_, _, _) -> true
        | ShrInt(_, _, _) -> true
        | AddFloat(_, _, _) -> true
        | SubFloat(_, _, _) -> true
        | MulFloat(_, _, _) -> true
        | DivFloat(_, _, _) -> true
        | IntToFloat(_, _) -> true
        | FloatToInt(_, _) -> true
        | CmpIntGt(_, _, _) -> true
        | CmpIntGe(_, _, _) -> true
        | CmpIntLt(_, _, _) -> true
        | CmpIntLe(_, _, _) -> true
        | CmpUIntGt(_, _, _) -> true
        | CmpUIntGe(_, _, _) -> true
        | CmpUIntLt(_, _, _) -> true
        | CmpUIntLe(_, _, _) -> true
        | CmpIntEq(_, _, _) -> true
        | CmpIntNe(_, _, _) -> true
        | CmpFloatGt(_, _, _) -> true
        | CmpFloatGe(_, _, _) -> true
        | CmpFloatLt(_, _, _) -> true
        | CmpFloatLe(_, _, _) -> true
        | CmpFloatEq(_, _, _) -> true
        | CmpFloatNe(_, _, _) -> true
        | CmpStrEq(_, _, _) -> true
        | CmpStrNe(_, _, _) -> true
        | GetAdtTag(_, _) -> true
        | LoadArgumentOwnership(_) -> true
        | LoadFuncAddr(_, _) -> true
        | SaveArenaState(_, _, _) -> true
        | RestoreArenaState(_, _, _, _) -> true
        | ReclaimArenaChunks(_, _, _) -> true
        | SaveStackPointer(_) -> true
        | RestoreStackPointer(_) -> true
        | _ -> false

// Negative, so it can never collide with a real temp: the identity of the value the backend's
// entry prologue stores into env/arg slot 0/1 before any instruction this pass can see.
let entrySlotIdentity (slot: IrLocal) = -1 - slot

let emptyLocalCseState hasEnvAndArgParams =
    LocalCseState(
        fieldCache = Ashes.Collection.Map.empty,
        callCache = Ashes.Collection.Map.empty,
        valueOf = Ashes.Collection.Map.empty,
        slotValue = if hasEnvAndArgParams
        then
            Ashes.Collection.Map.empty
            |> intSet(0)(entrySlotIdentity(0))
            |> intSet(1)(entrySlotIdentity(1))
        else Ashes.Collection.Map.empty,
        freshPointers = Ashes.Collection.Map.empty
    )

let recursive resolveCseValue (valueOf: MapTree(Int, Maybe(Int))) (temp: Int) =
    match optGet(temp)(valueOf) with
        | Some(alias) -> resolveCseValue(valueOf)(alias)
        | None -> temp

// The field cache is keyed by pointer identity, then by field.
let lookupFieldCache (ptr: Int) (field: Int) (cache: MapTree(Int, MapTree(Int, IrTemp))) =
    match intGet(ptr)(cache) with
        | Some(fields) -> intGet(field)(fields)
        | None -> None

let rememberField (ptr: Int) (field: Int) (value: IrTemp) (cache: MapTree(Int, MapTree(Int, IrTemp))) =
    (let fields =
        match intGet(ptr)(cache) with
            | Some(known) -> known
            | None -> Ashes.Collection.Map.empty
    in
        intSet(ptr)(intSet(field)(value)(fields))(cache))

let callKeyMatches (env: Int) (arg: Int) (flag: Int) (stackAllocated: Bool) (key: (Int, Int, Int, Bool)) =
    match key with
        | (cachedEnv, cachedArg, cachedFlag, cachedStack) ->
            if cachedEnv == env
            then
                if cachedArg == arg
                then
                    if cachedFlag == flag
                    then cachedStack == stackAllocated
                    else false
                else false
            else false

let recursive lookupCallEntry (env: Int) (arg: Int) (flag: Int) (stackAllocated: Bool) (entries: List(((Int, Int, Int, Bool), IrTemp))) =
    match entries with
        | [] -> None
        | (key, cached) :: tail ->
            if callKeyMatches(env)(arg)(flag)(stackAllocated)(key)
            then Some(cached)
            else lookupCallEntry(env)(arg)(flag)(stackAllocated)(tail)

// The call cache is keyed by callee label; the calls of one callee are told apart by operands.
let lookupCallCache (label: Str) env arg flag stackAllocated (cache: MapTree(Str, List(((Int, Int, Int, Bool), IrTemp)))) =
    match strGet(label)(cache) with
        | Some(entries) -> lookupCallEntry(env)(arg)(flag)(stackAllocated)(entries)
        | None -> None

let rememberCall (label: Str) env arg flag stackAllocated (dest: IrTemp) (cache: MapTree(Str, List(((Int, Int, Int, Bool), IrTemp)))) =
    (let entries =
        match strGet(label)(cache) with
            | Some(known) -> known
            | None -> []
    in strSet(label)(((env, arg, flag, stackAllocated), dest) :: entries)(cache))

let cseInvalidateCaches (state: LocalCseState) = state with fieldCache = Ashes.Collection.Map.empty, callCache = Ashes.Collection.Map.empty

// Pure value-identity aliases: the alias map records the canonical value so the caches can be
// keyed by value rather than by raw temp.
let trackLocalCseAlias inst (state: LocalCseState) =
    match inst with
        | Borrow(target, source) ->
            Some((state with valueOf = optSet(target)(resolveCseValue(state.valueOf)(source))(state.valueOf)))
        | RcDup(target, source, _, _) ->
            Some((state with valueOf = optSet(target)(resolveCseValue(state.valueOf)(source))(state.valueOf)))
        | StoreLocal(slot, source) ->
            Some((state with slotValue = intSet(slot)(resolveCseValue(state.valueOf)(source))(state.slotValue)))
        | LoadLocal(target, slot) ->
            match intGet(slot)(state.slotValue) with
                | Some(known) -> Some((state with valueOf = optSet(target)(known)(state.valueOf)))
                | None -> Some((state with valueOf = optForget(target)(state.valueOf)))
        | _ -> None

// A cache hit becomes a Borrow copy of the first occurrence's result, exactly the idiom the
// identity reduction uses, so the ownership-copy elision that follows forwards and erases it.
// A pointer allocated in this same block is fresh: nothing that existed before it can hold or
// derive a reference to it, so a SetAdtField through it populates the field cache with exactly
// that one entry instead of invalidating everything, and the next read of the same field forwards
// the stored value (the construct-then-destructure shape). The cached value is the write's raw
// source temp, never its canonical identity: canonicalization can resolve to the env/arg slot
// sentinel, which is only ever a key and must not be emitted.
let eliminateLocalCseInstruction (evaluable: MapTree(Str, IrFunction)) inst loc (state: LocalCseState) =
    match inst with
        | GetAdtField(target, ptr, field, _tagless) ->
            let key = resolveCseValue(state.valueOf)(ptr)
            in
                match lookupFieldCache(key)(field)(state.fieldCache) with
                    | Some(cached) -> ((state with valueOf = optSet(target)(cached)(state.valueOf)), IrInstruction(instruction = Borrow(target)(cached), location = loc))
                    | None -> ((state with fieldCache = rememberField(key)(field)(target)(state.fieldCache)), IrInstruction(instruction = inst, location = loc))
        | CallKnown(dest, label, envTemp, argTemp, flagTemp, stackAllocated) ->
            if isEvaluableLabel(label)(evaluable)
            then
                let env = resolveCseValue(state.valueOf)(envTemp)
                in
                    let arg = resolveCseValue(state.valueOf)(argTemp)
                    in
                        let flag = resolveCseValue(state.valueOf)(flagTemp)
                        in
                            match lookupCallCache(label)(env)(arg)(flag)(stackAllocated)(state.callCache) with
                                | Some(cached) -> ((state with valueOf = optSet(dest)(cached)(state.valueOf)), IrInstruction(instruction = Borrow(dest)(cached), location = loc))
                                | None -> ((state with callCache = rememberCall(label)(env)(arg)(flag)(stackAllocated)(dest)(state.callCache)), IrInstruction(instruction = inst, location = loc))
            else (cseInvalidateCaches(state), IrInstruction(instruction = inst, location = loc))
        | AllocAdt(target, _, _, _, _) -> ((state with freshPointers = intSet(target)(true)(state.freshPointers)), IrInstruction(instruction = inst, location = loc))
        | AllocAdtStack(target, _, _, _) -> ((state with freshPointers = intSet(target)(true)(state.freshPointers)), IrInstruction(instruction = inst, location = loc))
        | SetAdtField(ptr, field, source, _tagless) ->
            let key = resolveCseValue(state.valueOf)(ptr)
            in
                if intHas(key)(state.freshPointers)
                then ((state with fieldCache = rememberField(key)(field)(source)(state.fieldCache)), IrInstruction(instruction = inst, location = loc))
                else (cseInvalidateCaches(state), IrInstruction(instruction = inst, location = loc))
        | _ ->
            if isLocalCseSafeInstruction(inst)
            then (state, IrInstruction(instruction = inst, location = loc))
            else (cseInvalidateCaches(state), IrInstruction(instruction = inst, location = loc))

// Slots 0 (env) and 1 (arg) are populated by the backend's entry prologue, a native store never
// visible as a StoreLocal, so they are seeded with a stable identity at function entry and again
// at every label: without it every read of a function's own argument looks like an unknown value.
// `initial` is that seeded state, built once per function.
let recursive eliminateLocalRedundantComputationPass evaluable (initial: LocalCseState) instructions (state: LocalCseState) acc =
    match instructions with
        | [] -> reverse(acc)
        | (IrInstruction { instruction = Label(_) } as labelInst) :: tail -> eliminateLocalRedundantComputationPass(evaluable)(initial)(tail)(initial)(labelInst :: acc)
        | (IrInstruction { instruction = inst, location = loc } as irInst) :: tail ->
            match trackLocalCseAlias(inst)(state) with
                | Some(nextState) -> eliminateLocalRedundantComputationPass(evaluable)(initial)(tail)(nextState)(irInst :: acc)
                | None ->
                    match eliminateLocalCseInstruction(evaluable)(inst)(loc)(state) with
                        | (nextState, rewritten) -> eliminateLocalRedundantComputationPass(evaluable)(initial)(tail)(nextState)(rewritten :: acc)

let eliminateLocalRedundantComputation evaluable hasEnvAndArgParams instructions =
    (let initial = emptyLocalCseState(hasEnvAndArgParams)
    in eliminateLocalRedundantComputationPass(evaluable)(initial)(instructions)(initial)([]))

// Closure environment scalarization. A stack closure with one or two 8-byte scalar captures
// whose only use is already a devirtualized CallKnown packs those values through an AllocStack +
// StoreMemOffset + LoadEnv round trip although they never need to leave a register. When the
// callee touches its environment only through LoadEnv, the captured values are passed directly in
// the call's existing three-word ABI: the first capture in the env word, a second in the
// ownership-flag word, which is free whenever the call passes no flag and the callee never reads
// one (LoadArgumentOwnership is a raw read of that same parameter, so the variant reads the second
// capture through it). A generated callee variant, memoized per label and capture count, reads
// them as raw parameters; the original callee is never rewritten, since another use of the same
// label may still need the pointer-based form. Three or more captures keep their environment: the
// shared signature has no further free word.
// The first temp stored through each (base pointer, offset) pair of a function body.
let recursive collectFirstStoredWords instructions (acc: MapTree(Int, MapTree(Int, IrTemp))) =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = StoreMemOffset(storeBase, storeOffset, source) } :: tail ->
            let byOffset =
                match intGet(storeBase)(acc) with
                    | Some(known) -> known
                    | None -> Ashes.Collection.Map.empty
            in
                if intHas(storeOffset)(byOffset)
                then collectFirstStoredWords(tail)(acc)
                else
                    acc
                    |> intSet(storeBase)(intSet(storeOffset)(source)(byOffset))
                    |> collectFirstStoredWords(tail)
        | _ :: tail -> collectFirstStoredWords(tail)(acc)

let findStoredWord (basePtr: IrTemp) (offset: Int) (stores: MapTree(Int, MapTree(Int, IrTemp))) =
    match intGet(basePtr)(stores) with
        | Some(byOffset) -> intGet(offset)(byOffset)
        | None -> None

let recursive readsEnvPointerRaw instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadLocal(_, 0) } :: _ -> true
        | _ :: tail -> readsEnvPointerRaw(tail)

let recursive readsArgumentOwnership instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadArgumentOwnership(_) } :: _ -> true
        | _ :: tail -> readsArgumentOwnership(tail)

// (every LoadEnv index is below the capture count, at least one LoadEnv seen)
let recursive loadEnvShape (captureCount: Int) instructions sawLoadEnv =
    match instructions with
        | [] -> (true, sawLoadEnv)
        | IrInstruction { instruction = LoadEnv(_, index) } :: tail ->
            if index < captureCount
            then loadEnvShape(captureCount)(tail)(true)
            else (false, sawLoadEnv)
        | _ :: tail -> loadEnvShape(captureCount)(tail)(sawLoadEnv)

let recursive replaceLoadEnvWithParameterReads instructions acc =
    match instructions with
        | [] -> reverse(acc)
        | IrInstruction { instruction = LoadEnv(target, index), location = loc } :: tail ->
            let read =
                if index == 0
                then LoadLocal(target)(0)
                else LoadArgumentOwnership(target)
            in replaceLoadEnvWithParameterReads(tail)(IrInstruction(instruction = read, location = loc) :: acc)
        | head :: tail -> replaceLoadEnvWithParameterReads(tail)(head :: acc)

let buildScalarEnvVariant (callee: IrFunction) (captureCount: Int) (counter: Int) =
    match loadEnvShape(captureCount)(callee.instructions)(false) with
        | (true, true) ->
            Some(
                callee with label = callee.label + "__scalarenv" + Ashes.Trait.Show.show(counter), instructions = replaceLoadEnvWithParameterReads(callee.instructions)([])
            )
        | _ -> None

// A real lowered closure reads a capture only through LoadEnv, which dereferences slot 0 inside
// its own codegen; a raw LoadLocal of slot 0 means the pointer serves some other purpose. A
// coroutine's state-machine transform reads captures against its own frame instead, a shape this
// pass does not attempt. When the second capture is to travel in the flag word, a callee that
// reads the flag itself cannot take it.
let tryBuildScalarEnvVariant (callee: IrFunction) (captureCount: Int) (counter: Int) =
    if callee.hasEnvAndArgParams
    then
        match callee.coroutine with
            | Some(_) -> None
            | None ->
                if readsEnvPointerRaw(callee.instructions)
                then None
                else
                    if captureCount == 2
                    then
                        if readsArgumentOwnership(callee.instructions)
                        then None
                        else buildScalarEnvVariant(callee)(captureCount)(counter)
                    else buildScalarEnvVariant(callee)(captureCount)(counter)
    else None

// The memo is keyed by the callee's label and then by its capture count, never by a string
// built from the two. A concatenated key is a freshly allocated string whose only owner is the memo
// it is stored into, and stage 1 miscompiled exactly that: the key was released at this function's
// scope exit while the returned state still held it, the next call's own key reused the freed cell,
// and the lookup then matched a different callee's entry and handed back its variant. Keying on
// the two fields in turn allocates no key, so there is no reference for the lifetime to get wrong.
let lookupScalarEnvVariant (label: Str) (captureCount: Int) (entries: MapTree(Str, MapTree(Int, Maybe(Str)))) =
    match strGet(label)(entries) with
        | Some(byCount) -> intGet(captureCount)(byCount)
        | None -> None

// A callee is decided once and never revised.
let rememberScalarEnvVariant (label: Str) (captureCount: Int) (variant: Maybe(Str)) (state: ScalarizeState) =
    (let byCount =
        match strGet(label)(state.variantByCallee) with
            | Some(known) -> known
            | None -> Ashes.Collection.Map.empty
    in
        state with variantByCallee = strSet(label)(intSet(captureCount)(variant)(byCount))(state.variantByCallee))

let getOrCreateScalarEnvVariant (label: Str) (captureCount: Int) (functions: MapTree(Str, IrFunction)) (state: ScalarizeState) =
    match lookupScalarEnvVariant(label)(captureCount)(state.variantByCallee) with
        | Some(memoized) -> (memoized, state)
        | None ->
            match strGet(label)(functions) with
                | None -> (None, rememberScalarEnvVariant(label)(captureCount)(None)(state))
                | Some(callee) ->
                    match tryBuildScalarEnvVariant(callee)(captureCount)(state.counter) with
                        | None -> (None, rememberScalarEnvVariant(label)(captureCount)(None)(state))
                        | Some(variant) -> (Some(variant.label), (rememberScalarEnvVariant(label)(captureCount)(Some(variant.label))(state) with newFunctions = variant :: state.newFunctions, counter = state.counter + 1))

// The caller-side shape: the call's env temp is defined once by an 8- or 16-byte AllocStack,
// filled by exactly one store per 8-byte capture, and used nowhere else (those stores and this
// call), so there is no other read and no escape; a second capture can only travel in the flag
// word when this call passes no ownership flag.
let scalarEnvSiteShapeMatches (envSize: Int) (flagTemp: IrTemp) (useCount: Maybe(Int)) =
    if envSize == 8
    then useCount == Some(2)
    else
        if envSize == 16
        then
            if flagTemp < 0
            then useCount == Some(3)
            else false
        else false

// Each accepted site, by env temp: (variant label, first captured word, second captured word or -1).
let recursive collectScalarEnvCallSites stores insts singleDefs useCounts functions (state: ScalarizeState) acc =
    match insts with
        | [] -> (acc, state)
        | IrInstruction { instruction = CallKnown(_, label, envTemp, _, flagTemp, true) } :: tail ->
            match intGet(envTemp)(singleDefs) with
                | Some(AllocStack(_, envSize)) ->
                    if useCounts
                    |> intGet(envTemp)
                    |> scalarEnvSiteShapeMatches(envSize)(flagTemp)
                    then
                        match findStoredWord(envTemp)(0)(stores) with
                            | None -> collectScalarEnvCallSites(stores)(tail)(singleDefs)(useCounts)(functions)(state)(acc)
                            | Some(first) ->
                                let second =
                                    if envSize == 16
                                    then findStoredWord(envTemp)(8)(stores)
                                    else Some(-1)
                                in
                                    match second with
                                        | None -> collectScalarEnvCallSites(stores)(tail)(singleDefs)(useCounts)(functions)(state)(acc)
                                        | Some(secondWord) ->
                                            match getOrCreateScalarEnvVariant(label)(envSize / 8)(functions)(state) with
                                                | (Some(variantLabel), nextState) ->
                                                    acc
                                                    |> intSet(envTemp)((variantLabel, first, secondWord))
                                                    |> collectScalarEnvCallSites(stores)(tail)(singleDefs)(useCounts)(functions)(nextState)
                                                | (None, nextState) -> collectScalarEnvCallSites(stores)(tail)(singleDefs)(useCounts)(functions)(nextState)(acc)
                    else collectScalarEnvCallSites(stores)(tail)(singleDefs)(useCounts)(functions)(state)(acc)
                | _ -> collectScalarEnvCallSites(stores)(tail)(singleDefs)(useCounts)(functions)(state)(acc)
        | _ :: tail -> collectScalarEnvCallSites(stores)(tail)(singleDefs)(useCounts)(functions)(state)(acc)

// The retargeted call of an accepted site: the variant takes the first captured word as its env
// word and the second, when there is one, as its flag word.
let scalarEnvCall (dest: IrTemp) (argTemp: IrTemp) (flagTemp: IrTemp) (site: (Str, IrTemp, IrTemp)) =
    match site with
        | (variantLabel, first, second) ->
            if second < 0
            then CallKnown(dest)(variantLabel)(first)(argTemp)(flagTemp)(false)
            else CallKnown(dest)(variantLabel)(first)(argTemp)(second)(false)

// The env temp of an accepted site is used exactly by its stores and its call, so dropping the
// allocation and the stores by env temp and retargeting the call is exact.
let recursive rewriteScalarEnvCallSites insts (sites: MapTree(Int, (Str, IrTemp, IrTemp))) acc =
    match insts with
        | [] -> reverse(acc)
        | (IrInstruction { instruction = AllocStack(target, _) } as irInst) :: tail ->
            match intGet(target)(sites) with
                | Some(_) -> rewriteScalarEnvCallSites(tail)(sites)(acc)
                | None -> rewriteScalarEnvCallSites(tail)(sites)(irInst :: acc)
        | (IrInstruction { instruction = StoreMemOffset(basePtr, _, _) } as irInst) :: tail ->
            match intGet(basePtr)(sites) with
                | Some(_) -> rewriteScalarEnvCallSites(tail)(sites)(acc)
                | None -> rewriteScalarEnvCallSites(tail)(sites)(irInst :: acc)
        | (IrInstruction { instruction = CallKnown(dest, _, envTemp, argTemp, flagTemp, true), location = loc } as irInst) :: tail ->
            match intGet(envTemp)(sites) with
                | Some(site) -> rewriteScalarEnvCallSites(tail)(sites)(IrInstruction(instruction = scalarEnvCall(dest)(argTemp)(flagTemp)(site), location = loc) :: acc)
                | None -> rewriteScalarEnvCallSites(tail)(sites)(irInst :: acc)
        | head :: tail -> rewriteScalarEnvCallSites(tail)(sites)(head :: acc)

let recursive collectStackCallEnvironments instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = CallKnown(_, _, envTemp, _, _, true) } :: tail ->
            acc
            |> intSet(envTemp)(true)
            |> collectStackCallEnvironments(tail)
        | _ :: tail -> collectStackCallEnvironments(tail)(acc)

// Only the env temps of stack-environment calls are ever asked about, so the three tables below
// cover just those temps and are empty for a body with no such call.
let stackCallDefinitions instructions =
    (let wanted = collectStackCallEnvironments(instructions)(Ashes.Collection.Map.empty)
    in
        if Ashes.Collection.Map.isEmpty(wanted)
        then Ashes.Collection.Map.empty
        else singleDefinitionsOfWanted(instructions)(wanted))

let stackCallUseCounts instructions =
    (let wanted = collectStackCallEnvironments(instructions)(Ashes.Collection.Map.empty)
    in
        if Ashes.Collection.Map.isEmpty(wanted)
        then Ashes.Collection.Map.empty
        else useCountsOfWanted(instructions)(wanted)(Ashes.Collection.Map.empty))

let stackCallStoredWords instructions =
    (let wanted = collectStackCallEnvironments(instructions)(Ashes.Collection.Map.empty)
    in
        if Ashes.Collection.Map.isEmpty(wanted)
        then Ashes.Collection.Map.empty
        else collectFirstStoredWords(instructions)(Ashes.Collection.Map.empty))

let scalarizeCallSitesInFunction functions (state: ScalarizeState) (fn: IrFunction) =
    (let singleDefs = stackCallDefinitions(fn.instructions)
    in
        let useCounts = stackCallUseCounts(fn.instructions)
        in
            let stores = stackCallStoredWords(fn.instructions)
            in
                match collectScalarEnvCallSites(stores)(fn.instructions)(singleDefs)(useCounts)(functions)(state)(Ashes.Collection.Map.empty) with
                    | (sites, nextState) ->
                        if Ashes.Collection.Map.isEmpty(sites)
                        then (fn, nextState)
                        else ((fn with instructions = rewriteScalarEnvCallSites(fn.instructions)(sites)([])), nextState))

let recursive scalarizeCallSitesInFunctions functions (state: ScalarizeState) remaining acc =
    match remaining with
        | [] -> (reverse(acc), state)
        | fn :: tail ->
            match scalarizeCallSitesInFunction(functions)(state)(fn) with
                | (rewritten, nextState) -> scalarizeCallSitesInFunctions(functions)(nextState)(tail)(rewritten :: acc)

// Generated variants are appended after every caller has been rewritten; the callee lookup uses
// the original functions, since a variant is never itself a scalarization target.
let scalarizeSingleCaptureStackClosures (entry: IrFunction) (functions: List(IrFunction)) =
    (let initialState = ScalarizeState(variantByCallee = Ashes.Collection.Map.empty, newFunctions = [], counter = 0)
    in
        let byLabel = functionsByLabel(functions)(Ashes.Collection.Map.empty)
        in
            match scalarizeCallSitesInFunction(byLabel)(initialState)(entry) with
                | (newEntry, entryState) ->
                    match scalarizeCallSitesInFunctions(byLabel)(entryState)(functions)([]) with
                        | (newFunctions, finalState) ->
                            (newEntry, finalState.newFunctions
                            |> reverse
                            |> append(newFunctions)))

// Captured-closure devirtualization. A stitched module refers to its sibling functions through
// alias bindings that lambdas capture, so a call such as `parserCurrent(state)` inside another
// parser function loads the callee from its own environment and calls it indirectly. Every site
// that creates that lambda's closure stores the same known closure into the same environment
// word (the alias is bound once), so the call target is statically known after all: a
// CallClosure on a LoadEnv whose word resolves, at every creation site of the enclosing
// function, to one closure label becomes a CallKnown with the closure object's own environment
// word, exactly as the returned-closure devirtualization does for a curried second application.
// Resolution follows single-definition temps, single-store local slots, Borrow copies,
// known-returned call results, and the creating function's own captured words (to a
// whole-program fixpoint). A word with a disagreeing or unresolvable site is never rewritten.
// Tables keyed by a function label and then a word index.
let slotGet (label: Str) (index: Int) table =
    match strGet(label)(table) with
        | Some(byIndex) -> intGet(index)(byIndex)
        | None -> None

let slotSet (label: Str) (index: Int) value table =
    (let byIndex =
        match strGet(label)(table) with
            | Some(known) -> known
            | None -> Ashes.Collection.Map.empty
    in
        strSet(label)(intSet(index)(value)(byIndex))(table))

let slotHas (label: Str) (index: Int) table =
    match slotGet(label)(index)(table) with
        | Some(_) -> true
        | None -> false

// Every temp stored through each (base pointer, offset) pair of a function body.
let lookupEnvironmentStores (basePtr: IrTemp) (offset: Int) (entries: MapTree(Int, MapTree(Int, List(IrTemp)))) =
    match intGet(basePtr)(entries) with
        | Some(byOffset) -> intGet(offset)(byOffset)
        | None -> None

let recursive collectEnvironmentStores instructions (acc: MapTree(Int, MapTree(Int, List(IrTemp)))) =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = StoreMemOffset(basePtr, offset, source) } :: tail ->
            let byOffset =
                match intGet(basePtr)(acc) with
                    | Some(known) -> known
                    | None -> Ashes.Collection.Map.empty
            in
                let sources =
                    match intGet(offset)(byOffset) with
                        | Some(existing) -> source :: existing
                        | None -> [source]
                in
                    acc
                    |> intSet(basePtr)(intSet(offset)(sources)(byOffset))
                    |> collectEnvironmentStores(tail)
        | _ :: tail -> collectEnvironmentStores(tail)(acc)

// A closure whose only use was an immediate call has already been devirtualized into a
// CallKnown over its environment, so such a call is a creation site too. The environment size
// comes from the fresh allocation when there is one (a CallKnown carries none itself):
// (label, environment temp, environment size, whether the environment is a fresh allocation).
let describeCreationSite (facts: ClosureDefinitionFacts) inst =
    (let described =
        match inst with
            | MakeClosure(_, label, envTemp, size, _, _, _, _) -> Some((label, envTemp, size))
            | MakeClosureStack(_, label, envTemp, size, _, _, _) -> Some((label, envTemp, size))
            | CallKnown(_, label, envTemp, _, _, _) -> Some((label, envTemp, 0))
            | _ -> None
    in
        match described with
            | None -> None
            | Some((label, envTemp, declaredSize)) ->
                match intGet(envTemp)(facts.singleDefs) with
                    | Some(Alloc(_, size, _)) -> Some((label, envTemp, size, true))
                    | Some(AllocStack(_, size)) -> Some((label, envTemp, size, true))
                    | _ -> Some((label, envTemp, declaredSize, false)))

// What a stored temp holds, followed through single-definition temps, single-store local slots,
// Borrow copies and known-returned call results. Everything here is local to the creating
// function, so it is settled once; only a captured word of the creator itself is left for the
// whole-program fixpoint.
let recursive resolveCaptureSource (creatorLabel: Str) (facts: ClosureDefinitionFacts) (temp: IrTemp) knownReturned (depth: Int) =
    if depth > 16
    then SourceUnknown
    else
        match intGet(temp)(facts.singleDefs) with
            | Some(MakeClosure(_, label, _, _, _, _, _, _)) -> SourceLabel(label)
            | Some(MakeClosureStack(_, label, _, _, _, _, _)) -> SourceLabel(label)
            | Some(Borrow(_, source)) -> resolveCaptureSource(creatorLabel)(facts)(source)(knownReturned)(depth + 1)
            | Some(LoadLocal(_, slot)) ->
                if intGet(slot)(facts.storeCountBySlot) == Some(1)
                then
                    match intGet(slot)(facts.singleStoreSourceBySlot) with
                        | Some(source) -> resolveCaptureSource(creatorLabel)(facts)(source)(knownReturned)(depth + 1)
                        | None -> SourceUnknown
                else SourceUnknown
            | Some(LoadEnv(_, index)) -> SourceEnvWord(creatorLabel)(index)
            | Some(CallKnown(_, callee, _, _, _, _)) ->
                match strGet(callee)(knownReturned) with
                    | Some(label) -> SourceLabel(label)
                    | None -> SourceUnknown
            | _ -> SourceUnknown

let addCaptureSource (label: Str) (index: Int) (source: CaptureSource) groups =
    (let members =
        match slotGet(label)(index)(groups) with
            | Some(existing) -> source :: existing
            | None -> [source]
    in slotSet(label)(index)(members)(groups))

// Every closure creation with a non-empty environment contributes one source per environment
// word, grouped by the (label, word) pair it fills; a word stored more than once, not at all,
// through an environment pointer that is not a single fresh allocation, or from a temp nothing
// is provable about marks the whole pair unresolvable.
let recursive addCaptureSitesForWords (label: Str) (envTemp: IrTemp) (fresh: Bool) (index: Int) (wordCount: Int) stores (creatorLabel: Str) (facts: ClosureDefinitionFacts) knownReturned groups unresolvable =
    if index >= wordCount
    then (groups, unresolvable)
    else
        let single =
            if fresh
            then
                match lookupEnvironmentStores(envTemp)(index * 8)(stores) with
                    | Some(source :: []) -> resolveCaptureSource(creatorLabel)(facts)(source)(knownReturned)(0)
                    | _ -> SourceUnknown
            else SourceUnknown
        in
            match single with
                | SourceUnknown ->
                    unresolvable
                    |> slotSet(label)(index)(true)
                    |> addCaptureSitesForWords(label)(envTemp)(fresh)(index + 1)(wordCount)(stores)(creatorLabel)(facts)(knownReturned)(groups)
                | source ->
                    addCaptureSitesForWords(label)(envTemp)(fresh)(index + 1)(wordCount)(stores)(creatorLabel)(facts)(knownReturned)(addCaptureSource(label)(index)(source)(groups))(unresolvable)

let recursive collectCaptureSitesInBody instructions stores (creatorLabel: Str) (facts: ClosureDefinitionFacts) knownReturned groups unresolvable =
    match instructions with
        | [] -> (groups, unresolvable)
        | IrInstruction { instruction = inst } :: tail ->
            match describeCreationSite(facts)(inst) with
                | Some((label, envTemp, envSize, fresh)) ->
                    if envSize > 0
                    then
                        match addCaptureSitesForWords(label)(envTemp)(fresh)(0)(envSize / 8)(stores)(creatorLabel)(facts)(knownReturned)(groups)(unresolvable) with
                            | (nextGroups, nextUnresolvable) -> collectCaptureSitesInBody(tail)(stores)(creatorLabel)(facts)(knownReturned)(nextGroups)(nextUnresolvable)
                    else collectCaptureSitesInBody(tail)(stores)(creatorLabel)(facts)(knownReturned)(groups)(unresolvable)
                | None -> collectCaptureSitesInBody(tail)(stores)(creatorLabel)(facts)(knownReturned)(groups)(unresolvable)

// A creation site has a non-empty environment only through a closure construction or an
// environment allocation, so a body with neither contributes nothing and is not tabled.
let recursive mayCreateEnvironment instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = MakeClosure(_, _, _, _, _, _, _, _) } :: _ -> true
        | IrInstruction { instruction = MakeClosureStack(_, _, _, _, _, _, _) } :: _ -> true
        | IrInstruction { instruction = Alloc(_, _, _) } :: _ -> true
        | IrInstruction { instruction = AllocStack(_, _) } :: _ -> true
        | _ :: tail -> mayCreateEnvironment(tail)

// The definition facts capture resolution reads; use counts and droppers are not among them.
let collectCaptureDefinitionFacts (parameterSlots: List(Int)) instructions =
    (let defCounts = defCountsOf(instructions)
    in
        match countStoresBySlot(instructions)(implicitParameterStores(parameterSlots)(Ashes.Collection.Map.empty))(Ashes.Collection.Map.empty) with
            | (storeCounts, storeSources) ->
                ClosureDefinitionFacts(
                    defCounts = defCounts,
                    useCounts = Ashes.Collection.Map.empty,
                    singleDefs = singleDefsOf(instructions)(defCounts),
                    storeCountBySlot = storeCounts,
                    singleStoreSourceBySlot = storeSources,
                    closureTempsWithDropper = Ashes.Collection.Map.empty
                ))

let recursive collectCaptureSites (functions: List(IrFunction)) knownReturned groups unresolvable =
    match functions with
        | [] -> (groups, unresolvable)
        | fn :: tail ->
            if mayCreateEnvironment(fn.instructions)
            then
                match collectCaptureSitesInBody(fn.instructions)(collectEnvironmentStores(fn.instructions)(Ashes.Collection.Map.empty))(fn.label)(collectCaptureDefinitionFacts(parameterSlotsOf(fn))(fn.instructions))(knownReturned)(groups)(unresolvable) with
                    | (nextGroups, nextUnresolvable) -> collectCaptureSites(tail)(knownReturned)(nextGroups)(nextUnresolvable)
            else collectCaptureSites(tail)(knownReturned)(groups)(unresolvable)

// The groups still to settle, as (label, word, sources): every group not already unresolvable.
let pendingCaptureGroups groups unresolvable =
    (let addLabel acc (label: Str) byIndex =
        let addIndex inner (index: Int) sources =
            if slotHas(label)(index)(unresolvable)
            then inner
            else (label, index, sources) :: inner
        in Ashes.Collection.Map.foldLeft(addIndex)(acc)(byIndex)
    in Ashes.Collection.Map.foldLeft(addLabel)([])(groups))

let agreeOnCaptureLabel (label: Str) (agreed: Maybe(Str)) =
    match agreed with
        | None -> Some(Some(label))
        | Some(previous) ->
            if previous == label
            then Some(agreed)
            else None

// A group is known once every source resolves to the same label; one disagreeing source settles
// it as unknown, and an unresolved captured word of a creator leaves it pending.
let recursive resolveCaptureGroup (sources: List(CaptureSource)) known (agreed: Maybe(Str)) (pending: Bool) =
    match sources with
        | [] ->
            if pending
            then CapturePending
            else
                match agreed with
                    | Some(label) -> CaptureKnown(label)
                    | None -> CapturePending
        | source :: tail ->
            match source with
                | SourceUnknown -> CaptureUnknown
                | SourceLabel(label) ->
                    match agreeOnCaptureLabel(label)(agreed) with
                        | Some(nextAgreed) -> resolveCaptureGroup(tail)(known)(nextAgreed)(pending)
                        | None -> CaptureUnknown
                | SourceEnvWord(creatorLabel, index) ->
                    match slotGet(creatorLabel)(index)(known) with
                        | Some(label) ->
                            match agreeOnCaptureLabel(label)(agreed) with
                                | Some(nextAgreed) -> resolveCaptureGroup(tail)(known)(nextAgreed)(pending)
                                | None -> CaptureUnknown
                        | None -> resolveCaptureGroup(tail)(known)(agreed)(true)

let recursive resolveCaptureGroups (groups: List((Str, Int, List(CaptureSource)))) known (pending: List((Str, Int, List(CaptureSource)))) (changed: Bool) =
    match groups with
        | [] -> (known, pending, changed)
        | group :: tail ->
            match group with
                | (label, index, sources) ->
                    match resolveCaptureGroup(sources)(known)(None)(false) with
                        | CaptureUnknown -> resolveCaptureGroups(tail)(known)(pending)(true)
                        | CaptureKnown(resolved) ->
                            resolveCaptureGroups(tail)(slotSet(label)(index)(resolved)(known))(pending)(true)
                        | CapturePending -> resolveCaptureGroups(tail)(known)(group :: pending)(changed)

// Whole-program fixpoint over the capture graph: each pass settles the groups whose sources
// resolve against the words known so far, so a chain of captured aliases converges while a word
// that depends on an unresolvable one stays pending and is never rewritten. Only the groups still
// pending are revisited.
let recursive computeKnownCapturedClosureLabels (groups: List((Str, Int, List(CaptureSource)))) known =
    match resolveCaptureGroups(groups)(known)([])(false) with
        | (nextKnown, pending, true) -> computeKnownCapturedClosureLabels(pending)(nextKnown)
        | (nextKnown, _, false) -> nextKnown

let recursive resolveCapturedWordIndex singleDefs (temp: IrTemp) (depth: Int) =
    if depth > 8
    then None
    else
        match intGet(temp)(singleDefs) with
            | Some(LoadEnv(_, index)) -> Some(index)
            | Some(Borrow(_, source)) -> resolveCapturedWordIndex(singleDefs)(source)(depth + 1)
            | _ -> None

// A CallClosure through a known captured word becomes a read of the closure object's environment
// word (offset 8) and a direct CallKnown of the proven label at the same position. `knownWords`
// holds the enclosing function's own proven words by index.
let recursive devirtualizeCapturedClosureCallsPass (knownWords: MapTree(Int, Str)) singleDefs insts (nextTemp: Int) acc =
    match insts with
        | [] -> (reverse(acc), nextTemp)
        | (IrInstruction { instruction = CallClosure(dest, closureTemp, argTemp, flagTemp), location = loc } as irInst) :: tail ->
            let resolved =
                match resolveCapturedWordIndex(singleDefs)(closureTemp)(0) with
                    | Some(index) -> intGet(index)(knownWords)
                    | None -> None
            in
                match resolved with
                    | Some(label) ->
                        devirtualizeCapturedClosureCallsPass(knownWords)(singleDefs)(tail)(nextTemp + 1)(
                            IrInstruction(instruction = CallKnown(dest)(label)(nextTemp)(argTemp)(flagTemp)(false), location = loc) :: IrInstruction(instruction = LoadMemOffset(nextTemp)(closureTemp)(8), location = loc) :: acc
                        )
                    | None -> devirtualizeCapturedClosureCallsPass(knownWords)(singleDefs)(tail)(nextTemp)(irInst :: acc)
        | head :: tail -> devirtualizeCapturedClosureCallsPass(knownWords)(singleDefs)(tail)(nextTemp)(head :: acc)

// The enclosing function's own proven words by index; empty when it has none.
let knownCapturedWordsOf (label: Str) knownCaptured =
    match strGet(label)(knownCaptured) with
        | Some(words) -> words
        | None -> Ashes.Collection.Map.empty

// A function none of whose captured words is proven has nothing to resolve and is not tabled.
let capturedCallDefinitions knownWords instructions =
    if Ashes.Collection.Map.isEmpty(knownWords)
    then Ashes.Collection.Map.empty
    else
        instructions
        |> defCountsOf
        |> singleDefsOf(instructions)

let devirtualizeCapturedClosureCallsInFunction knownCaptured (fn: IrFunction) =
    if fn.hasEnvAndArgParams && containsCallClosure(fn.instructions)
    then
        match fn.coroutine with
            | Some(_) -> fn
            | None ->
                let knownWords = knownCapturedWordsOf(fn.label)(knownCaptured)
                in
                    match devirtualizeCapturedClosureCallsPass(knownWords)(capturedCallDefinitions(knownWords)(fn.instructions))(fn.instructions)(fn.tempCount)([]) with
                        | (rewritten, nextTemp) ->
                            if nextTemp == fn.tempCount
                            then fn
                            else fn with instructions = rewritten, tempCount = nextTemp
    else fn

let devirtualizeCapturedClosureCalls (entry: IrFunction) (functions: List(IrFunction)) =
    (let all = entry :: functions
    in
        let knownReturned = computeKnownReturnedClosureLabels(all)
        in
            match collectCaptureSites(all)(knownReturned)(Ashes.Collection.Map.empty)(Ashes.Collection.Map.empty) with
                | (groups, unresolvable) ->
                    let knownCaptured =
                        computeKnownCapturedClosureLabels(pendingCaptureGroups(groups)(unresolvable))(Ashes.Collection.Map.empty)
                    in
                        if Ashes.Collection.Map.isEmpty(knownCaptured)
                        then (entry, functions)
                        else
                            (devirtualizeCapturedClosureCallsInFunction(knownCaptured)(entry), map(devirtualizeCapturedClosureCallsInFunction(knownCaptured))(functions)))

// Currying-stage inlining. A curried function of several parameters lowers to a chain of stages,
// each of which only copies its captures and its argument into a fresh environment and returns
// the next stage's closure. Once the calls along a saturated chain are direct, the caller still
// pays one heap environment and one closure object per stage. When the stage's whole body is
// that copy, the caller can build the next stage's environment itself, on its own stack, and
// call the next function directly with it: the stage's closure object is never needed, and the
// environment dies with the caller's frame. The next function must read its environment only
// through LoadEnv (no raw read of the environment pointer, no coroutine frame rewrite), and the
// call must not become a native sibling tail call, which the stack-allocated flag on CallKnown
// already enforces. Repeated to a fixpoint so a chain of stages collapses into the innermost call.
let acceptStageClosure (scan: CurryingStageScan) (target: IrTemp) (label: Str) (envPtr: IrTemp) (size: Int) =
    if scan.scanClosureTemp < 0
    then
        if envPtr == scan.scanEnvTemp
        then
            if size == scan.scanEnvSize
            then Some((scan with scanClosureTemp = target, scanNextLabel = Some(label)))
            else None
        else None
    else None

// A pure stage: one fresh environment allocation, loads of its own captures and argument,
// exactly one store per environment word from those loads, one closure construction over that
// environment (not itself reference-counted, so skipping it drops no release), and a return of
// that closure as the last instruction. Anything else disqualifies the function.
let acceptCurryingStageInstruction (scan: CurryingStageScan) inst (last: Bool) =
    match inst with
        | Alloc(target, size, runtimeManaged) ->
            if runtimeManaged
            then None
            else
                if scan.scanEnvTemp < 0
                then Some((scan with scanEnvTemp = target, scanEnvSize = size))
                else None
        | AllocStack(target, size) ->
            if scan.scanEnvTemp < 0
            then Some((scan with scanEnvTemp = target, scanEnvSize = size))
            else None
        | LoadEnv(target, index) -> Some((scan with sourceIndexByTemp = setTempAssociation(target)(index)(scan.sourceIndexByTemp)))
        | LoadLocal(target, slot) ->
            if slot == 1
            then Some((scan with sourceIndexByTemp = setTempAssociation(target)(-1)(scan.sourceIndexByTemp)))
            else None
        | StoreMemOffset(basePtr, offset, source) ->
            if scan.scanEnvTemp >= 0
            then
                if basePtr == scan.scanEnvTemp
                then
                    match lookupTempAssociation(source)(scan.sourceIndexByTemp) with
                        | Some(captureIndex) -> Some((scan with scanStores = (offset, captureIndex) :: scan.scanStores))
                        | None -> None
                else None
            else None
        | MakeClosure(target, label, envPtr, size, runtimeManaged, _, _, _) ->
            if runtimeManaged
            then None
            else acceptStageClosure(scan)(target)(label)(envPtr)(size)
        | MakeClosureStack(target, label, envPtr, size, _, _, _) -> acceptStageClosure(scan)(target)(label)(envPtr)(size)
        | Return(source) ->
            if last
            then
                if scan.scanClosureTemp >= 0
                then
                    if source == scan.scanClosureTemp
                    then Some(scan)
                    else None
                else None
            else None
        | _ -> None

let recursive stageStoresAreWellFormed (envSize: Int) (stores: List((Int, Int))) (seenOffsets: List(Int)) =
    match stores with
        | [] -> true
        | (offset, _) :: tail ->
            if offset < 0
            then false
            else
                if offset >= envSize
                then false
                else
                    if offset % 8 != 0
                    then false
                    else
                        if intListContains(offset)(seenOffsets)
                        then false
                        else stageStoresAreWellFormed(envSize)(tail)(offset :: seenOffsets)

let finishCurryingStageScan (scan: CurryingStageScan) =
    match scan.scanNextLabel with
        | None -> None
        | Some(label) ->
            if scan.scanEnvSize <= 0
            then None
            else
                let stores = reverse(scan.scanStores)
                in
                    if length(stores) != scan.scanEnvSize / 8
                    then None
                    else
                        if stageStoresAreWellFormed(scan.scanEnvSize)(stores)([])
                        then Some(CurryingStage(envSizeBytes = scan.scanEnvSize, stores = stores, nextLabel = label))
                        else None

let recursive scanCurryingStage (scan: CurryingStageScan) instructions =
    match instructions with
        | [] -> finishCurryingStageScan(scan)
        | IrInstruction { instruction = inst } :: tail ->
            let last =
                match tail with
                    | [] -> true
                    | _ -> false
            in
                match acceptCurryingStageInstruction(scan)(inst)(last) with
                    | None -> None
                    | Some(next) -> scanCurryingStage(next)(tail)

let tryMatchCurryingStage (fn: IrFunction) =
    if fn.hasEnvAndArgParams
    then
        match fn.coroutine with
            | Some(_) -> None
            | None ->
                if length(fn.instructions) < 3
                then None
                else
                    scanCurryingStage(
                        CurryingStageScan(scanEnvTemp = -1, scanEnvSize = 0, scanClosureTemp = -1, scanNextLabel = None, sourceIndexByTemp = [], scanStores = [])
                    )(
                        fn.instructions
                    )
    else None

// The stage shape of every function that has one, by label, computed once from the bodies as
// they are before this pass rewrites any caller.
let recursive collectCurryingStages (functions: List(IrFunction)) (acc: MapTree(Str, CurryingStage)) =
    match functions with
        | [] -> acc
        | fn :: tail ->
            match tryMatchCurryingStage(fn) with
                | Some(stage) ->
                    acc
                    |> strSet(fn.label)(stage)
                    |> collectCurryingStages(tail)
                | None -> collectCurryingStages(tail)(acc)

let calleeAcceptsCallerFrameEnvironment (callee: IrFunction) =
    if callee.hasEnvAndArgParams
    then
        match callee.coroutine with
            | Some(_) -> false
            | None -> !readsEnvPointerRaw(callee.instructions)
    else false

// Whether the function each stage returns can take a caller-frame environment, by that
// function's label, decided once per label rather than once per call site.
let nextStageAcceptance (stages: MapTree(Str, CurryingStage)) (functions: MapTree(Str, IrFunction)) =
    (let decide acc (stageLabel: Str) (stage: CurryingStage) =
        if strHas(stage.nextLabel)(acc)
        then acc
        else
            match strGet(stage.nextLabel)(functions) with
                | Some(next) ->
                    strSet(stage.nextLabel)(calleeAcceptsCallerFrameEnvironment(next))(acc)
                | None -> strSet(stage.nextLabel)(false)(acc)
    in Ashes.Collection.Map.foldLeft(decide)(Ashes.Collection.Map.empty)(stages))

// The LoadMemOffset that reads each closure temp's environment word, as (position, word temp) by
// closure temp, and the CallKnown that passes each environment temp, as its position and as the
// instruction itself, each by environment temp.
let recursive indexStageChainSites instructions (position: Int) (envLoads: MapTree(Int, (Int, Int))) (callPositions: MapTree(Int, Int)) (callInstructions: MapTree(Int, IrInstruction)) =
    match instructions with
        | [] -> (envLoads, callPositions, callInstructions)
        | irInst :: tail ->
            match irInst with
                | IrInstruction { instruction = LoadMemOffset(envWord, basePtr, 8) } ->
                    indexStageChainSites(tail)(position + 1)(intSet(basePtr)((position, envWord))(envLoads))(callPositions)(callInstructions)
                | IrInstruction { instruction = CallKnown(_, _, envTemp, _, _, _) } ->
                    callInstructions
                    |> intSet(envTemp)(irInst)
                    |> indexStageChainSites(tail)(position + 1)(envLoads)(intSet(envTemp)(position)(callPositions))
                | _ -> indexStageChainSites(tail)(position + 1)(envLoads)(callPositions)(callInstructions)

let knownCallTargets (label: Str) (call: IrInstruction) =
    match call with
        | IrInstruction { instruction = CallKnown(_, callee, _, _, _, _) } -> callee == label
        | _ -> false

// The chain: `r = CallKnown(stage, env, a)`, `e = LoadMemOffset(r, 8)`, `CallKnown(next, e, b)`,
// with r and e each used exactly once, next being the label the stage returns, and next able to
// take a caller-frame environment: (load position, next call position, next call, stage).
let tryMatchInlinableStageChain (stageLabel: Str) (resultTemp: IrTemp) useCounts (envLoads: MapTree(Int, (Int, Int))) (callPositions: MapTree(Int, Int)) (callInstructions: MapTree(Int, IrInstruction)) (stages: MapTree(Str, CurryingStage)) (accepting: MapTree(Str, Bool)) =
    match strGet(stageLabel)(stages) with
        | None -> None
        | Some(stage) ->
            if intGet(resultTemp)(useCounts) == Some(1)
            then
                match intGet(resultTemp)(envLoads) with
                    | Some((loadPosition, envWord)) ->
                        if intGet(envWord)(useCounts) == Some(1)
                        then
                            match intGet(envWord)(callInstructions) with
                                | Some(nextCall) ->
                                    if knownCallTargets(stage.nextLabel)(nextCall)
                                    then
                                        if strGet(stage.nextLabel)(accepting) == Some(true)
                                        then
                                            match intGet(envWord)(callPositions) with
                                                | Some(callPosition) -> Some((loadPosition, callPosition, nextCall, stage))
                                                | None -> None
                                        else None
                                    else None
                                | None -> None
                        else None
                    | None -> None
            else None

// Emits the stack environment the stage would have built: its captures come from the stage's
// own environment (the pointer the call passes), its argument from the call's argument temp.
let recursive buildStageEnvironmentStores (stageEnv: IrTemp) (argTemp: IrTemp) (stackEnv: IrTemp) (stores: List((Int, Int))) (nextTemp: Int) acc =
    match stores with
        | [] -> (reverse(acc), nextTemp)
        | (offset, captureIndex) :: tail ->
            if captureIndex < 0
            then buildStageEnvironmentStores(stageEnv)(argTemp)(stackEnv)(tail)(nextTemp)(IrInstruction(instruction = StoreMemOffset(stackEnv)(offset)(argTemp), location = None) :: acc)
            else
                buildStageEnvironmentStores(stageEnv)(argTemp)(stackEnv)(tail)(nextTemp + 1)(
                    IrInstruction(instruction = StoreMemOffset(stackEnv)(offset)(nextTemp), location = None) :: IrInstruction(instruction = LoadMemOffset(nextTemp)(stageEnv)(captureIndex * 8), location = None) :: acc
                )

// A chain whose final call re-enters the enclosing function itself is a recursive back edge: a
// per-iteration `AllocStack` would grow the caller's frame until the stack guard, so the
// environment goes on the heap instead (the stand-in allocator's usual leak-not-miscompile trade)
// and the retargeted call keeps a heap-environment flag — the exact shape the backend's
// `musttail` fusion accepts, so the loop runs in constant stack.
let buildStageEnvironment (stageEnv: IrTemp) (argTemp: IrTemp) location (stage: CurryingStage) (selfEdge: Bool) (nextTemp: Int) =
    (let stackEnv = nextTemp
    in
        let allocation =
            if selfEdge
            then Alloc(stackEnv)(stage.envSizeBytes)(false)
            else AllocStack(stackEnv)(stage.envSizeBytes)
        in
            match buildStageEnvironmentStores(stageEnv)(argTemp)(stackEnv)(stage.stores)(nextTemp + 1)([]) with
                | (stores, finalTemp) -> (IrInstruction(instruction = allocation, location = location) :: stores, stackEnv, finalTemp))

let retargetKnownCallEnvironment (stackEnv: IrTemp) (stackAllocated: Bool) irInst =
    match irInst with
        | IrInstruction { instruction = CallKnown(dest, label, _, argTemp, flagTemp, _), location = loc } -> IrInstruction(instruction = CallKnown(dest)(label)(stackEnv)(argTemp)(flagTemp)(stackAllocated), location = loc)
        | other -> other

// A chain whose final call re-enters the enclosing function itself.
let stageChainReentersOwnFunction (ownLabel: Str) (nextCall: IrInstruction) =
    match nextCall with
        | IrInstruction { instruction = CallKnown(_, nextLabel, _, _, _, _) } -> nextLabel == ownLabel
        | _ -> false

// Each accepted site: the stage call's position mapped to its replacement, the position of the
// environment-word load to drop, and the next call's position mapped to its retargeted form. A
// call already retargeted in this pass is left for the next iteration.
let recursive collectStageExpansions (ownLabel: Str) remaining (position: Int) useCounts envLoads callPositions callInstructions stages accepting (nextTemp: Int) expansions removed rewrites =
    match remaining with
        | [] -> (expansions, removed, rewrites, nextTemp)
        | irInst :: tail ->
            match irInst with
                | IrInstruction { instruction = CallKnown(resultTemp, stageLabel, envTemp, argTemp, _, _), location = loc } ->
                    if intHas(position)(rewrites)
                    then collectStageExpansions(ownLabel)(tail)(position + 1)(useCounts)(envLoads)(callPositions)(callInstructions)(stages)(accepting)(nextTemp)(expansions)(removed)(rewrites)
                    else
                        match tryMatchInlinableStageChain(stageLabel)(resultTemp)(useCounts)(envLoads)(callPositions)(callInstructions)(stages)(accepting) with
                            | None -> collectStageExpansions(ownLabel)(tail)(position + 1)(useCounts)(envLoads)(callPositions)(callInstructions)(stages)(accepting)(nextTemp)(expansions)(removed)(rewrites)
                            | Some((loadPosition, callPosition, nextCall, stage)) ->
                                let selfEdge = stageChainReentersOwnFunction(ownLabel)(nextCall)
                                in
                                    match buildStageEnvironment(envTemp)(argTemp)(loc)(stage)(selfEdge)(nextTemp) with
                                        | (replacement, stackEnv, finalTemp) ->
                                            let retargeted = retargetKnownCallEnvironment(stackEnv)(selfEdge == false)(nextCall)
                                            in
                                                collectStageExpansions(ownLabel)(tail)(position + 1)(useCounts)(envLoads)(callPositions)(callInstructions)(stages)(accepting)(finalTemp)(intSet(position)(replacement)(expansions))(intSet(loadPosition)(true)(removed))(
                                                    intSet(callPosition)(retargeted)(rewrites)
                                                )
                | _ -> collectStageExpansions(ownLabel)(tail)(position + 1)(useCounts)(envLoads)(callPositions)(callInstructions)(stages)(accepting)(nextTemp)(expansions)(removed)(rewrites)

let recursive rebuildWithStageExpansions instructions (position: Int) expansions removed rewrites acc =
    match instructions with
        | [] -> reverse(acc)
        | irInst :: tail ->
            if intHas(position)(removed)
            then rebuildWithStageExpansions(tail)(position + 1)(expansions)(removed)(rewrites)(acc)
            else
                match intGet(position)(expansions) with
                    | Some(replacement) ->
                        acc
                        |> append(reverse(replacement))
                        |> rebuildWithStageExpansions(tail)(position + 1)(expansions)(removed)(rewrites)
                    | None ->
                        match intGet(position)(rewrites) with
                            | Some(rewritten) -> rebuildWithStageExpansions(tail)(position + 1)(expansions)(removed)(rewrites)(rewritten :: acc)
                            | None -> rebuildWithStageExpansions(tail)(position + 1)(expansions)(removed)(rewrites)(irInst :: acc)

let recursive callsCurryingStage (stages: MapTree(Str, CurryingStage)) instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CallKnown(_, label, _, _, _, _) } :: tail ->
            if strHas(label)(stages)
            then true
            else callsCurryingStage(stages)(tail)
        | _ :: tail -> callsCurryingStage(stages)(tail)

// The use counts and chain sites a body needs, empty for a body that calls no stage.
let stageCallUseCounts stages instructions =
    if callsCurryingStage(stages)(instructions)
    then useCountsOf(instructions)
    else Ashes.Collection.Map.empty

let stageChainSites stages instructions =
    if callsCurryingStage(stages)(instructions)
    then indexStageChainSites(instructions)(0)(Ashes.Collection.Map.empty)(Ashes.Collection.Map.empty)(Ashes.Collection.Map.empty)
    else (Ashes.Collection.Map.empty, Ashes.Collection.Map.empty, Ashes.Collection.Map.empty)

// Every expansion takes at least one new temp, so an unchanged temp count means no chain matched.
let inlineCurryingStagesOnce stages accepting (fn: IrFunction) =
    (let useCounts = stageCallUseCounts(stages)(fn.instructions)
    in
        match stageChainSites(stages)(fn.instructions) with
            | (envLoads, callPositions, callInstructions) ->
                match collectStageExpansions(fn.label)(fn.instructions)(0)(useCounts)(envLoads)(callPositions)(callInstructions)(stages)(accepting)(fn.tempCount)(Ashes.Collection.Map.empty)(Ashes.Collection.Map.empty)(Ashes.Collection.Map.empty) with
                    | (expansions, removed, rewrites, nextTemp) ->
                        if nextTemp == fn.tempCount
                        then (fn, false)
                        else ((fn with instructions = rebuildWithStageExpansions(fn.instructions)(0)(expansions)(removed)(rewrites)([]), tempCount = nextTemp), true))

let recursive inlineCurryingStagesInFunction stages accepting (fn: IrFunction) =
    match inlineCurryingStagesOnce(stages)(accepting)(fn) with
        | (_, false) -> fn
        | (rewritten, true) -> inlineCurryingStagesInFunction(stages)(accepting)(rewritten)

let inlineCurryingStages (entry: IrFunction) (functions: List(IrFunction)) =
    (let all = entry :: functions
    in
        let stages = collectCurryingStages(all)(Ashes.Collection.Map.empty)
        in
            if Ashes.Collection.Map.isEmpty(stages)
            then (entry, functions)
            else
                let accepting =
                    Ashes.Collection.Map.empty
                    |> functionsByLabel(all)
                    |> nextStageAcceptance(stages)
                in
                    (inlineCurryingStagesInFunction(stages)(accepting)(entry), map(inlineCurryingStagesInFunction(stages)(accepting))(functions)))

// Control-flow simplification. Three locally safe rewrites that need no reachability analysis:
// a branch aimed at a label that is immediately followed by nothing but an unconditional Jump is
// redirected straight to that chain's final destination; a label with no remaining branch
// reference is dropped (only a marker goes, never the code around it); and a Jump immediately
// followed by its own target label is a redundant fall-through (nothing can branch to a Jump
// itself, only to a label). Iterated together with unreachable-code elimination to a fixed point:
// redirecting several branches to one final label and dropping the labels that used to separate
// them stacks unconditional Jumps back-to-back, every one after the first is newly unreachable, and
// removing those can bring a surviving Jump adjacent to its own target.
// The direct hop of every empty label: (label, the target of the Jump that follows it).
let recursive collectEmptyLabelHops instructions (acc: MapTree(Str, Str)) =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = Label(name) } :: tail ->
            match tail with
                | IrInstruction { instruction = Jump(target) } :: _ ->
                    if name == target
                    then collectEmptyLabelHops(tail)(acc)
                    else
                        acc
                        |> strSet(name)(target)
                        |> collectEmptyLabelHops(tail)
                | _ -> collectEmptyLabelHops(tail)(acc)
        | _ :: tail -> collectEmptyLabelHops(tail)(acc)

// Follows a chain of hops to its final destination, stopping on a label already visited so a
// pathological Jump-only loop in the source program cannot recurse forever.
let recursive chaseRedirectChain (hops: MapTree(Str, Str)) (current: Str) (seen: List(Str)) =
    match strGet(current)(hops) with
        | None -> current
        | Some(next) ->
            if strListContains(next)(seen)
            then current
            else chaseRedirectChain(hops)(next)(next :: seen)

let resolveRedirects (hops: MapTree(Str, Str)) =
    (let resolve acc (name: Str) (target: Str) =
        strSet(name)(chaseRedirectChain(hops)(name)([name]))(acc)
    in Ashes.Collection.Map.foldLeft(resolve)(Ashes.Collection.Map.empty)(hops))

let redirectedLabel (redirect: MapTree(Str, Str)) (label: Str) =
    match strGet(label)(redirect) with
        | Some(target) -> target
        | None -> label

let redirectSwitchCase redirect (switchCase: IrSwitchCase) = IrSwitchCase(tag = switchCase.tag, label = redirectedLabel(redirect)(switchCase.label))

let recursive rewriteBranchTargets instructions redirect acc =
    match instructions with
        | [] -> reverse(acc)
        | IrInstruction { instruction = Jump(target), location = loc } :: tail ->
            rewriteBranchTargets(tail)(redirect)(IrInstruction(instruction = target
            |> redirectedLabel(redirect)
            |> Jump, location = loc) :: acc)
        | IrInstruction { instruction = JumpIfFalse(condition, target), location = loc } :: tail ->
            rewriteBranchTargets(tail)(redirect)(IrInstruction(instruction = target
            |> redirectedLabel(redirect)
            |> JumpIfFalse(condition), location = loc) :: acc)
        | IrInstruction { instruction = SwitchTag(tagTemp, cases, defaultLabel), location = loc } :: tail ->
            rewriteBranchTargets(tail)(redirect)(IrInstruction(instruction = defaultLabel
            |> redirectedLabel(redirect)
            |> SwitchTag(tagTemp)(map(redirectSwitchCase(redirect))(cases)), location = loc) :: acc)
        | head :: tail -> rewriteBranchTargets(tail)(redirect)(head :: acc)

let recursive dropUnreferencedLabels instructions (refs: MapTree(Str, Int)) acc =
    match instructions with
        | [] -> reverse(acc)
        | (IrInstruction { instruction = Label(name) } as irInst) :: tail ->
            match strGet(name)(refs) with
                | Some(_) -> dropUnreferencedLabels(tail)(refs)(irInst :: acc)
                | None -> dropUnreferencedLabels(tail)(refs)(acc)
        | head :: tail -> dropUnreferencedLabels(tail)(refs)(head :: acc)

let recursive elideRedundantFallthroughJumps instructions acc =
    match instructions with
        | [] -> reverse(acc)
        | (IrInstruction { instruction = Jump(target) } as irInst) :: tail ->
            match tail with
                | IrInstruction { instruction = Label(name) } :: _ ->
                    if target == name
                    then elideRedundantFallthroughJumps(tail)(acc)
                    else elideRedundantFallthroughJumps(tail)(irInst :: acc)
                | _ -> elideRedundantFallthroughJumps(tail)(irInst :: acc)
        | head :: tail -> elideRedundantFallthroughJumps(tail)(head :: acc)

// Unreferenced labels go first, then redundant jumps, so a Jump/Label pair the label removal
// itself brings into adjacency is elided in the same pass.
let simplifyControlFlow instructions =
    (let hops = collectEmptyLabelHops(instructions)(Ashes.Collection.Map.empty)
    in
        let redirected =
            if Ashes.Collection.Map.isEmpty(hops)
            then instructions
            else
                rewriteBranchTargets(instructions)(resolveRedirects(hops))([])
        in
            let withoutUnreferencedLabels =
                dropUnreferencedLabels(redirected)(branchRefsOf(redirected))([])
            in elideRedundantFallthroughJumps(withoutUnreferencedLabels)([]))

let recursive instructionCount instructions (acc: Int) =
    match instructions with
        | [] -> acc
        | _ :: tail -> instructionCount(tail)(acc + 1)

// The instruction count strictly decreases on every iteration that changes anything (the one
// rewrite that removes nothing, a redirected target, resolves each chain fully on its first
// pass), which bounds the loop.
let recursive simplifyControlFlowToFixedPoint instructions =
    (let simplified =
        instructions
        |> simplifyControlFlow
        |> elideUnreachableCode
    in
        if instructionCount(simplified)(0) == instructionCount(instructions)(0)
        then simplified
        else simplifyControlFlowToFixedPoint(simplified))

let optimizeIrFunctionWithEvaluable evaluable (fn: IrFunction) =
    (let insts0 = fn.instructions
    in
        let insts1 = elideTrivialOwnershipCopies(insts0)
        in
            let insts2 = fuseAdjacentRuntimeRcPairs(insts1)
            in
                let insts3 =
                    devirtualizeKnownClosureCalls(parameterSlotsOf(fn))(insts2)
                in
                    let insts4 = foldConstants(insts3)
                    in
                        let insts5 =
                            insts4
                            |> reduceIdentitiesAndStrength
                            |> elideTrivialOwnershipCopies
                        in
                            let insts6 =
                                insts5
                                |> eliminateLocalRedundantComputation(evaluable)(fn.hasEnvAndArgParams)
                                |> elideTrivialOwnershipCopies
                            in
                                let insts7 = simplifyControlFlowToFixedPoint(insts6)
                                in
                                    let insts8 = elideDeadCode(insts7)
                                    in
                                        let insts9 = elideErasedRcDrops(insts8)
                                        in
                                            IrFunction(
                                                label = fn.label,
                                                instructions = insts9,
                                                localCount = fn.localCount,
                                                tempCount = fn.tempCount,
                                                hasEnvAndArgParams = fn.hasEnvAndArgParams,
                                                coroutine = fn.coroutine,
                                                localNames = fn.localNames,
                                                localTypes = fn.localTypes,
                                                origin = fn.origin,
                                                lifetimesPlaced = fn.lifetimesPlaced
                                            ))

// The per-function pipeline without a purity oracle: no known call is ever merged.
let optimizeIrFunction (fn: IrFunction) = optimizeIrFunctionWithEvaluable(Ashes.Collection.Map.empty)(fn)

// String-concatenation chain folding. A left-nested chain of ConcatStr links (`((a ++ b) ++ c) ++ d`)
// pays one allocation and one growing copy per link: n - 1 allocations and O(n^2) bytes copied
// for n parts. When every intermediate result is used exactly once, as the left operand of the
// next link, the chain folds into one ConcatStrN that allocates once for the sum of every part's
// length and copies each part into its final position. Runs as the very last step over the whole
// program, so every other pass only ever sees plain ConcatStr and only code generation needs the
// new shape. Folding delays reading an earlier part until the chain's end, so a chain is declined
// whenever an arena or stack bracket, a label, or a branch sits between the innermost part's
// definition and the fold point: a later part's own bracket could reclaim the memory the earlier
// part still has to be read from, invisible to a use-count check.
let recursive consumedAsConcatLeft instructions useCounts acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = ConcatStr(_, left, _, _) } :: tail ->
            if intGet(left)(useCounts) == Some(1)
            then
                acc
                |> intSet(left)(true)
                |> consumedAsConcatLeft(tail)(useCounts)
            else consumedAsConcatLeft(tail)(useCounts)(acc)
        | _ :: tail -> consumedAsConcatLeft(tail)(useCounts)(acc)

let isArenaOrControlFlowInstruction inst =
    match inst with
        | Label(_) -> true
        | Jump(_) -> true
        | JumpIfFalse(_, _) -> true
        | SwitchTag(_, _, _) -> true
        | SaveArenaState(_, _, _) -> true
        | RestoreArenaState(_, _, _, _) -> true
        | ReclaimArenaChunks(_, _, _) -> true
        | SaveStackPointer(_) -> true
        | RestoreStackPointer(_) -> true
        | _ -> false

// Walks inward from a root link through each left operand defined by exactly one ConcatStr with
// a single use and the same runtime-managed flag. Returns the parts in order (the innermost left
// operand first, then every right operand from innermost to outermost) and the absorbed inner
// link targets, innermost first.
let recursive collectConcatChain singleDefs useCounts (left: IrTemp) (managed: Bool) rights absorbed =
    match intGet(left)(singleDefs) with
        | Some(ConcatStr(innerTarget, innerLeft, innerRight, innerManaged)) ->
            if intGet(left)(useCounts) == Some(1)
            then
                if innerManaged == managed
                then collectConcatChain(singleDefs)(useCounts)(innerLeft)(managed)(innerRight :: rights)(innerTarget :: absorbed)
                else (left :: rights, absorbed)
            else (left :: rights, absorbed)
        | _ -> (left :: rights, absorbed)

let isRcDropInstruction inst =
    match inst with
        | RcDrop(_, _, _, _, _, _) -> true
        | _ -> false

// A release that yields a value or closes a resource is never moved past a fold.
let isUnsinkableRelease inst =
    match inst with
        | DropReuse(_, _, _, _) -> true
        | CleanupResource(_, _, _) -> true
        | _ -> false

// What one chain's range holds: the positions of its RcDrop instructions and the instructions
// themselves, both in their original order.
type ConcatChainReleases =
    | releasePositions: List(Int)
    | releaseInstructions: List(IrInstruction)

// Scans from the first instruction that defines the innermost part or the innermost link up to
// the root link. A bracket, label or branch in that range declines the fold, as does a release
// that yields a value or closes a resource. Otherwise the result is the range's RcDrop
// instructions: the lifetime placement releases an owned part right after the link that last
// reads it, and the fold reads every part at the root, so each such release moves to just after
// the folded instruction, in its original order (left in place it would free the part before the
// fold reads it).
let recursive chainRangeReleases instructions (innermostPart: IrTemp) (innermostLink: IrTemp) (rootTarget: IrTemp) scanning (position: Int) (positions: List(Int)) (releases: List(IrInstruction)) =
    match instructions with
        | [] -> Some(ConcatChainReleases(releasePositions = reverse(positions), releaseInstructions = reverse(releases)))
        | (IrInstruction { instruction = inst } as irInst) :: tail ->
            let defined = getDefinedTemps(inst)
            in
                let nowScanning =
                    if scanning
                    then true
                    else
                        if intListContains(innermostPart)(defined)
                        then true
                        else intListContains(innermostLink)(defined)
                in
                    if nowScanning
                    then
                        if isArenaOrControlFlowInstruction(inst)
                        then None
                        else
                            if intListContains(rootTarget)(defined)
                            then Some(ConcatChainReleases(releasePositions = reverse(positions), releaseInstructions = reverse(releases)))
                            else
                                if isUnsinkableRelease(inst)
                                then None
                                else
                                    if isRcDropInstruction(inst)
                                    then chainRangeReleases(tail)(innermostPart)(innermostLink)(rootTarget)(true)(position + 1)(position :: positions)(irInst :: releases)
                                    else chainRangeReleases(tail)(innermostPart)(innermostLink)(rootTarget)(true)(position + 1)(positions)(releases)
                    else chainRangeReleases(tail)(innermostPart)(innermostLink)(rootTarget)(false)(position + 1)(positions)(releases)

// Every fold of a body: the ConcatStrN that replaces each root (by root target), the absorbed
// inner links, the releases that follow each root's fold (by root target), and the positions
// those releases leave.
type ConcatFoldPlan =
    | foldsByRoot: MapTree(Int, IrInstructionKind)
    | absorbedLinks: MapTree(Int, Bool)
    | releasesAfterRoot: MapTree(Int, List(IrInstruction))
    | sunkPositions: MapTree(Int, Bool)

let emptyConcatFoldPlan = ConcatFoldPlan(foldsByRoot = Ashes.Collection.Map.empty, absorbedLinks = Ashes.Collection.Map.empty, releasesAfterRoot = Ashes.Collection.Map.empty, sunkPositions = Ashes.Collection.Map.empty)

let addConcatFold (target: IrTemp) (folded: IrInstructionKind) (absorbed: List(Int)) (releases: ConcatChainReleases) (plan: ConcatFoldPlan) =
    match (plan, releases) with
        | (ConcatFoldPlan { foldsByRoot = folds, absorbedLinks = links, releasesAfterRoot = after, sunkPositions = sunk }, ConcatChainReleases { releasePositions = positions, releaseInstructions = instructions }) -> ConcatFoldPlan(foldsByRoot = intSet(target)(folded)(folds), absorbedLinks = addAllToSet(absorbed)(links), releasesAfterRoot = intSet(target)(instructions)(after), sunkPositions = addAllToSet(positions)(sunk))

// Each fold, by root target, as the ConcatStrN that replaces the root; a chain needs at least one inner link.
let recursive collectConcatFolds allInsts instructions singleDefs useCounts consumedLefts (plan: ConcatFoldPlan) =
    match instructions with
        | [] -> plan
        | IrInstruction { instruction = ConcatStr(target, left, right, managed) } :: tail ->
            if intHas(target)(consumedLefts)
            then collectConcatFolds(allInsts)(tail)(singleDefs)(useCounts)(consumedLefts)(plan)
            else
                match collectConcatChain(singleDefs)(useCounts)(left)(managed)([right])([]) with
                    | (parts, absorbed) ->
                        match (parts, absorbed) with
                            | (innermostPart :: _, innermostLink :: _) ->
                                match chainRangeReleases(allInsts)(innermostPart)(innermostLink)(target)(false)(0)([])([]) with
                                    | Some(releases) ->
                                        plan
                                        |> addConcatFold(target)(ConcatStrN(target)(parts)(managed))(absorbed)(releases)
                                        |> collectConcatFolds(allInsts)(tail)(singleDefs)(useCounts)(consumedLefts)
                                    | None -> collectConcatFolds(allInsts)(tail)(singleDefs)(useCounts)(consumedLefts)(plan)
                            | _ -> collectConcatFolds(allInsts)(tail)(singleDefs)(useCounts)(consumedLefts)(plan)
        | _ :: tail -> collectConcatFolds(allInsts)(tail)(singleDefs)(useCounts)(consumedLefts)(plan)

let recursive prependReversed (items: List(IrInstruction)) (acc: List(IrInstruction)) =
    match items with
        | [] -> acc
        | item :: rest -> prependReversed(rest)(item :: acc)

let recursive rewriteConcatFolds instructions (plan: ConcatFoldPlan) (position: Int) acc =
    match instructions with
        | [] -> reverse(acc)
        | (IrInstruction { instruction = ConcatStr(target, _, _, _), location = loc } as irInst) :: tail ->
            if intHas(target)(plan.absorbedLinks)
            then rewriteConcatFolds(tail)(plan)(position + 1)(acc)
            else
                match intGet(target)(plan.foldsByRoot) with
                    | Some(folded) ->
                        let releases =
                            match intGet(target)(plan.releasesAfterRoot) with
                                | Some(found) -> found
                                | None -> []
                        in
                            IrInstruction(instruction = folded, location = loc) :: acc
                            |> prependReversed(releases)
                            |> rewriteConcatFolds(tail)(plan)(position + 1)
                    | None -> rewriteConcatFolds(tail)(plan)(position + 1)(irInst :: acc)
        | head :: tail ->
            if intHas(position)(plan.sunkPositions)
            then rewriteConcatFolds(tail)(plan)(position + 1)(acc)
            else rewriteConcatFolds(tail)(plan)(position + 1)(head :: acc)

let recursive collectConcatLefts instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = ConcatStr(_, left, _, _) } :: tail ->
            acc
            |> intSet(left)(true)
            |> collectConcatLefts(tail)
        | _ :: tail -> collectConcatLefts(tail)(acc)

// A chain is only ever followed through the left operands of ConcatStr links, so the two tables
// below cover just those temps and are empty for a body with no ConcatStr.
let concatLeftDefinitions instructions =
    (let wanted = collectConcatLefts(instructions)(Ashes.Collection.Map.empty)
    in
        if Ashes.Collection.Map.isEmpty(wanted)
        then Ashes.Collection.Map.empty
        else singleDefinitionsOfWanted(instructions)(wanted))

let concatLeftUseCounts instructions =
    (let wanted = collectConcatLefts(instructions)(Ashes.Collection.Map.empty)
    in
        if Ashes.Collection.Map.isEmpty(wanted)
        then Ashes.Collection.Map.empty
        else useCountsOfWanted(instructions)(wanted)(Ashes.Collection.Map.empty))

let foldConcatStrChains (fn: IrFunction) =
    (let singleDefs = concatLeftDefinitions(fn.instructions)
    in
        let useCounts = concatLeftUseCounts(fn.instructions)
        in
            let consumedLefts = consumedAsConcatLeft(fn.instructions)(useCounts)(Ashes.Collection.Map.empty)
            in
                match collectConcatFolds(fn.instructions)(fn.instructions)(singleDefs)(useCounts)(consumedLefts)(emptyConcatFoldPlan) with
                    | ConcatFoldPlan { foldsByRoot = folds } as plan ->
                        if Ashes.Collection.Map.isEmpty(folds)
                        then fn
                        else fn with instructions = rewriteConcatFolds(fn.instructions)(plan)(0)([]))

let optimizeIrProgramWithOptions (options: IrOptimizerOptions) (program: IrProgram) =
    (let programAfterCtEval =
        if options.enableCompileTimeEval
        then evaluateCompileTimeConstants(program)
        else program
    in
        let evaluable = computeEvaluableFunctions(programAfterCtEval)
        in
            let optEntry = optimizeIrFunctionWithEvaluable(evaluable)(programAfterCtEval.entryFunction)
            in
                let optFuncs =
                    map(optimizeIrFunctionWithEvaluable(evaluable))(programAfterCtEval.functions)
                in
                    match devirtualizeCapturedClosureCalls(optEntry)(optFuncs) with
                        | (capturedEntry, capturedFuncs) ->
                            let firstKnownLabels = computeKnownReturnedClosureLabels(capturedFuncs)
                            in
                                match capturedFuncs
                                |> map(devirtualizeReturnedClosureCallsInFunction(firstKnownLabels))
                                |> inlineCurryingStages(devirtualizeReturnedClosureCallsInFunction(firstKnownLabels)(capturedEntry)) with
                                    | (inlinedEntry, inlinedFuncs) ->
                                        match scalarizeSingleCaptureStackClosures(inlinedEntry)(inlinedFuncs) with
                                            | (scalEntry, scalFuncs) ->
                                                let knownLabels = computeKnownReturnedClosureLabels(scalFuncs)
                                                in
                                                    let devirtEntry = devirtualizeReturnedClosureCallsInFunction(knownLabels)(scalEntry)
                                                    in
                                                        let devirtFuncs =
                                                            map(devirtualizeReturnedClosureCallsInFunction(knownLabels))(scalFuncs)
                                                        in
                                                            let nonAllocating = computeNonAllocatingFunctions(devirtFuncs)
                                                            in
                                                                let finalEntry =
                                                                    devirtEntry
                                                                    |> stripRedundantArenaBrackets(nonAllocating)
                                                                    |> foldConcatStrChains
                                                                in
                                                                    let finalFuncs =
                                                                        devirtFuncs
                                                                        |> map(stripRedundantArenaBrackets(nonAllocating))
                                                                        |> map(foldConcatStrChains)
                                                                    in
                                                                        IrProgram(
                                                                            entryFunction = finalEntry,
                                                                            functions = finalFuncs,
                                                                            stringLiterals = programAfterCtEval.stringLiterals,
                                                                            externalFunctions = programAfterCtEval.externalFunctions,
                                                                            externalOpaqueTypes = programAfterCtEval.externalOpaqueTypes,
                                                                            usesPrintInt = programAfterCtEval.usesPrintInt,
                                                                            usesPrintStr = programAfterCtEval.usesPrintStr,
                                                                            usesPrintBool = programAfterCtEval.usesPrintBool,
                                                                            usesConcatStr = programAfterCtEval.usesConcatStr,
                                                                            usesClosures = programAfterCtEval.usesClosures,
                                                                            usesAsync = programAfterCtEval.usesAsync,
                                                                            capabilityHandlerGlobals = programAfterCtEval.capabilityHandlerGlobals,
                                                                            traitEvidence = programAfterCtEval.traitEvidence
                                                                        ))

let optimizeIrProgram program = optimizeIrProgramWithOptions(defaultOptimizerOptions)(program)
