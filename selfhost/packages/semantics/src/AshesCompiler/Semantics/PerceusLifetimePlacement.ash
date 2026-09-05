// Moves ordinary-value lifetime markers from lexical scope exits to control-flow precise last-use
// points, stage 0's `PerceusLifetimePlacement`. Lowering anchors an owned binding's release at its
// scope exit as `LoadLocal` of the owner slot followed by `RcDrop` naming that slot as `OwnerSlot`.
// For every slot with exactly one such anchor this pass removes the anchor (and its load), takes
// the region of blocks reachable from the binding's definition, dominated by it, and before the
// anchor, computes where the owner (through every alias: `Borrow`, a slot holding an alias past
// its store, an arena cell or closure environment embedding it, the result of a call receiving
// it) is still live, and re-inserts the drop, now naming the definition's source temp, after the
// last use in each block where the value dies, at the definition when it is never used, and at
// the entry of a block reached from a live branch when every predecessor arrives with the value
// live, else in a fresh block spliced into the live branch's edge
// (`<function>_rc_edge_<slot>_<block>`). A `CallClosure` that hands an alias to a callee while
// the owner stays live afterwards, and a record-field store of an alias, get a compensating
// `RcDup`. Resource cleanup
// (`CleanupResource`) is deliberately outside this pass. A function whose `lifetimesPlaced` flag
// is already set is returned unchanged.
import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrControlFlowGraph
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.StateMachineTransform
export (
    type PlacedInstructions(..),
    value placeLifetimes,
    value placeFunctionLifetimes,
    value placeInstructionLifetimes,
    value placeInstructionLifetimesIn,
)

type PlacedInstructions =
    | placedInstructions: List(IrInstruction)
    | placedTempCount: Int

type OwnerRegion =
    | regionDefinitionIndex: Int
    | regionBoundaryIndex: Int
    | regionDefinitionTemp: IrTemp

type OwnerAnchor =
    | anchorTypeName: Str
    | anchorRuntimeManaged: Bool
    | anchorMayBeEmpty: Bool
    | anchorStructuralDropper: Maybe(Str)
    | anchorLocation: Maybe(IrSourceLocation)

type BlockFacts =
    | factsBlock: Int
    | factsLoads: List(Int)
    | factsUses: List(Int)

type BlockLiveness =
    | livenessBlock: Int
    | livenessIn: Bool
    | livenessOut: Bool
    deriving {Eq}

// A `StoreLocal` of an alias into a slot, with the span of the block it sits in.
type AliasStore =
    | storeSlot: Int
    | storeIndex: Int
    | storeBlockStart: Int
    | storeBlockEnd: Int
    deriving {Eq}

type AliasState =
    | aliasTemps: List(Int)
    | aliasStores: List(AliasStore)

let recursive removeAt items index =
    match items with
        | [] -> []
        | head :: rest ->
            if index == 0
            then rest
            else head :: removeAt(rest)(index - 1)

let recursive insertAllAt index added items =
    if index == 0
    then append(added)(items)
    else
        match items with
            | [] -> added
            | head :: rest -> head :: insertAllAt(index - 1)(added)(rest)

// `(index, instruction)` pairs for the half-open index range `[start, end)`.
let recursive indexedRange (instructions: List(IrInstruction)) (index: Int) (start: Int) (end: Int) =
    match instructions with
        | [] -> []
        | head :: rest ->
            if index >= end
            then []
            else
                if index >= start
                then (index, head) :: indexedRange(rest)(index + 1)(start)(end)
                else indexedRange(rest)(index + 1)(start)(end)

let anchorSlot (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = RcDrop(_source, _typeName, ownerSlot, _runtimeManaged, _mayBeEmpty, _dropper) } -> ownerSlot
        | _ -> -1

// Every anchored owner slot in first-appearance order.
let recursive ownerSlots (instructions: List(IrInstruction)) (acc: List(Int)) =
    match instructions with
        | [] -> reverse(acc)
        | head :: rest ->
            head
            |> anchorSlot
            |> (given (slot) ->
                if slot >= 0 && containsInt(slot)(acc) == false
                then ownerSlots(rest)(slot :: acc)
                else ownerSlots(rest)(acc))

let recursive anchorIndices (instructions: List(IrInstruction)) (slot: Int) (index: Int) =
    match instructions with
        | [] -> []
        | head :: rest ->
            if anchorSlot(head) == slot
            then index :: anchorIndices(rest)(slot)(index + 1)
            else anchorIndices(rest)(slot)(index + 1)

// The first `StoreLocal` into `slot`, as `(index, source temp)`.
let recursive ownerDefinition (instructions: List(IrInstruction)) (slot: Int) (index: Int) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = StoreLocal(candidate, source) } :: rest ->
            if candidate == slot
            then Some((index, source))
            else ownerDefinition(rest)(slot)(index + 1)
        | _ :: rest -> ownerDefinition(rest)(slot)(index + 1)

let recursive reachableBeforeBoundary (blocks: List(IrCfgBlock)) (dominators: List(List(Int))) (start: Int) (boundary: Int) (pending: List(Int)) (reachable: List(Int)) =
    match pending with
        | [] -> reachable
        | current :: rest ->
            ((given (dominated) ->
                if current > boundary || dominated == false || sortedSetContains(current)(reachable)
                then reachableBeforeBoundary(blocks)(dominators)(start)(boundary)(rest)(reachable)
                else
                    if current == boundary
                    then
                        reachable
                        |> sortedSetInsert(current)
                        |> reachableBeforeBoundary(blocks)(dominators)(start)(boundary)(rest)
                    else
                        match listAt(blocks)(current) with
                            | Some(IrCfgBlock { blockSuccessors = successors }) ->
                                reachable
                                |> sortedSetInsert(current)
                                |> reachableBeforeBoundary(blocks)(dominators)(start)(boundary)(append(successors)(rest))
                            | None ->
                                reachable
                                |> sortedSetInsert(current)
                                |> reachableBeforeBoundary(blocks)(dominators)(start)(boundary)(rest)))(match listAt(dominators)(current) with
                | Some(set) -> sortedSetContains(start)(set)
                | None -> false)

let blockSpan (blocks: List(IrCfgBlock)) (index: Int) =
    match listAt(blocks)(index) with
        | Some(IrCfgBlock { blockStart = start, blockEnd = end }) -> (start, end)
        | None -> (0, 0)

let recursive regionInstructions (instructions: List(IrInstruction)) (blocks: List(IrCfgBlock)) (region: List(Int)) =
    match region with
        | [] -> []
        | blockIndex :: rest ->
            match blockSpan(blocks)(blockIndex) with
                | (start, end) ->
                    rest
                    |> regionInstructions(instructions)(blocks)
                    |> append(indexedRange(instructions)(0)(start)(end))

let recursive ownerLoadTargets (indexed: List((Int, IrInstruction))) (slot: Int) (acc: List(Int)) =
    match indexed with
        | [] -> acc
        | (_index, IrInstruction { instruction = LoadLocal(target, candidate) }) :: rest ->
            if candidate == slot
            then
                acc
                |> sortedSetInsert(target)
                |> ownerLoadTargets(rest)(slot)
            else ownerLoadTargets(rest)(slot)(acc)
        | _ :: rest -> ownerLoadTargets(rest)(slot)(acc)

let aliasTempsGrew (before: AliasState) (after: AliasState) = length(after.aliasTemps) > length(before.aliasTemps) || length(after.aliasStores) > length(before.aliasStores)

// The span of the block holding instruction `index`.
let recursive blockSpanContaining (blocks: List(IrCfgBlock)) (index: Int) =
    match blocks with
        | [] -> (0, 0)
        | IrCfgBlock { blockStart = start, blockEnd = end } :: rest ->
            if index >= start && index < end
            then (start, end)
            else blockSpanContaining(rest)(index)

let recursive containsStore (store: AliasStore) (stores: List(AliasStore)) =
    match stores with
        | [] -> false
        | head :: rest -> head == store || containsStore(store)(rest)

// A load reads an alias out of its slot only past a store of one: a store later in the load's
// own block is a different value (a loop parameter's successor stored after the old value was
// read for its release walk), while a store in any other block may reach the load.
let recursive loadSeesAliasStore (stores: List(AliasStore)) (slot: Int) (loadIndex: Int) (span: (Int, Int)) =
    match stores with
        | [] -> false
        | AliasStore { storeSlot = candidate, storeIndex = storeIndex, storeBlockStart = start, storeBlockEnd = end } :: rest ->
            match span with
                | (loadStart, loadEnd) ->
                    if candidate == slot && (start != loadStart || end != loadEnd || storeIndex < loadIndex)
                    then true
                    else loadSeesAliasStore(rest)(slot)(loadIndex)(span)

// One propagation step over a single instruction. An arena cell that embeds an alias without a
// reference of its own (a list literal's cons cell, a tuple, a closure environment) is an alias:
// every later use of the cell reads through to the owner. So is a closure made over such an
// environment, and the result of a call that receives an alias as its argument, closure, or
// environment: a curried stage's returned closure captured it, and a list-building loop conses a
// matched head into the list it returns, which the copy-out past the call window reads.
let propagateAlias (blocks: List(IrCfgBlock)) (state: AliasState) (index: Int) (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = Borrow(target, source) } ->
            if sortedSetContains(source)(state.aliasTemps)
            then state with aliasTemps = sortedSetInsert(target)(state.aliasTemps)
            else state
        | IrInstruction { instruction = StoreLocal(slot, source) } ->
            if sortedSetContains(source)(state.aliasTemps)
            then
                match blockSpanContaining(blocks)(index) with
                    | (start, end) ->
                        ((given (store) ->
                            if containsStore(store)(state.aliasStores)
                            then state
                            else state with aliasStores = store :: state.aliasStores))(AliasStore(storeSlot = slot, storeIndex = index, storeBlockStart = start, storeBlockEnd = end))
            else state
        | IrInstruction { instruction = LoadLocal(target, slot) } ->
            if index
            |> blockSpanContaining(blocks)
            |> loadSeesAliasStore(state.aliasStores)(slot)(index)
            then state with aliasTemps = sortedSetInsert(target)(state.aliasTemps)
            else state
        | IrInstruction { instruction = StoreMemOffset(basePtr, _offset, source) } ->
            if sortedSetContains(source)(state.aliasTemps)
            then state with aliasTemps = sortedSetInsert(basePtr)(state.aliasTemps)
            else state
        | IrInstruction { instruction = MakeClosure(target, _label, environmentPtr, _size, _managed, _returnsManaged, _acceptsManaged) } ->
            if sortedSetContains(environmentPtr)(state.aliasTemps)
            then state with aliasTemps = sortedSetInsert(target)(state.aliasTemps)
            else state
        | IrInstruction { instruction = MakeClosureStack(target, _label, environmentPtr, _size, _returnsManaged, _acceptsManaged) } ->
            if sortedSetContains(environmentPtr)(state.aliasTemps)
            then state with aliasTemps = sortedSetInsert(target)(state.aliasTemps)
            else state
        | IrInstruction { instruction = CallClosure(target, closureTemp, argument, _flag) } ->
            if sortedSetContains(argument)(state.aliasTemps) || sortedSetContains(closureTemp)(state.aliasTemps)
            then state with aliasTemps = sortedSetInsert(target)(state.aliasTemps)
            else state
        | IrInstruction { instruction = CallKnown(target, _label, environmentTemp, argument, _flag, _borrowed) } ->
            if sortedSetContains(argument)(state.aliasTemps) || sortedSetContains(environmentTemp)(state.aliasTemps)
            then state with aliasTemps = sortedSetInsert(target)(state.aliasTemps)
            else state
        | _ -> state

let recursive propagateAliases (blocks: List(IrCfgBlock)) (indexed: List((Int, IrInstruction))) (state: AliasState) =
    match indexed with
        | [] -> state
        | (index, instruction) :: rest ->
            instruction
            |> propagateAlias(blocks)(state)(index)
            |> propagateAliases(blocks)(rest)

let recursive aliasFixpoint (blocks: List(IrCfgBlock)) (indexed: List((Int, IrInstruction))) (state: AliasState) =
    state
    |> propagateAliases(blocks)(indexed)
    |> (given (next) ->
        if aliasTempsGrew(state)(next)
        then aliasFixpoint(blocks)(indexed)(next)
        else next)

// Every temp aliasing the owner within the region, to a fixpoint.
let collectOwnerAliases (blocks: List(IrCfgBlock)) (indexed: List((Int, IrInstruction))) (slot: Int) =
    AliasState(aliasTemps = ownerLoadTargets(indexed)(slot)([]), aliasStores = [])
    |> aliasFixpoint(blocks)(indexed)
    |> (given (state) -> state.aliasTemps)

let recursive anyTempIn (temps: List(Int)) (aliases: List(Int)) =
    match temps with
        | [] -> false
        | temp :: rest ->
            if sortedSetContains(temp)(aliases)
            then true
            else anyTempIn(rest)(aliases)

let recursive ownerUses (indexed: List((Int, IrInstruction))) (slot: Int) (aliases: List(Int)) =
    match indexed with
        | [] -> []
        | (index, IrInstruction { instruction = kind }) :: rest ->
            ((given (isUse) ->
                if isUse
                then index :: ownerUses(rest)(slot)(aliases)
                else ownerUses(rest)(slot)(aliases)))(match kind with
                | LoadLocal(_target, candidate) -> candidate == slot
                | _ ->
                    anyTempIn(getUsedTemps(kind))(aliases))

let recursive ownerLoads (indexed: List((Int, IrInstruction))) (slot: Int) =
    match indexed with
        | [] -> []
        | (index, IrInstruction { instruction = LoadLocal(_target, candidate) }) :: rest ->
            if candidate == slot
            then index :: ownerLoads(rest)(slot)
            else ownerLoads(rest)(slot)
        | _ :: rest -> ownerLoads(rest)(slot)

let recursive blockFactsFor (instructions: List(IrInstruction)) (blocks: List(IrCfgBlock)) (region: List(Int)) (slot: Int) (aliases: List(Int)) =
    match region with
        | [] -> []
        | blockIndex :: rest ->
            match blockSpan(blocks)(blockIndex) with
                | (start, end) ->
                    end
                    |> indexedRange(instructions)(0)(start)
                    |> (given (indexed) -> BlockFacts(factsBlock = blockIndex, factsLoads = ownerLoads(indexed)(slot), factsUses = ownerUses(indexed)(slot)(aliases)))
                    |> (given (facts) -> facts :: blockFactsFor(instructions)(blocks)(rest)(slot)(aliases))

let recursive factsOf (blockIndex: Int) (facts: List(BlockFacts)) =
    match facts with
        | [] -> BlockFacts(factsBlock = blockIndex, factsLoads = [], factsUses = [])
        | (BlockFacts { factsBlock = candidate } as entry) :: rest ->
            if candidate == blockIndex
            then entry
            else factsOf(blockIndex)(rest)

let recursive livenessOf (blockIndex: Int) (liveness: List(BlockLiveness)) =
    match liveness with
        | [] -> BlockLiveness(livenessBlock = blockIndex, livenessIn = false, livenessOut = false)
        | (BlockLiveness { livenessBlock = candidate } as entry) :: rest ->
            if candidate == blockIndex
            then entry
            else livenessOf(blockIndex)(rest)

let hasUse (blockIndex: Int) (facts: List(BlockFacts)) =
    match factsOf(blockIndex)(facts) with
        | BlockFacts { factsUses = uses } -> length(uses) > 0

let livenessInOf (blockIndex: Int) (liveness: List(BlockLiveness)) =
    match livenessOf(blockIndex)(liveness) with
        | BlockLiveness { livenessIn = liveIn } -> liveIn

let livenessOutOf (blockIndex: Int) (liveness: List(BlockLiveness)) =
    match livenessOf(blockIndex)(liveness) with
        | BlockLiveness { livenessOut = liveOut } -> liveOut

let recursive anyLiveSuccessor (successors: List(Int)) (region: List(Int)) (liveness: List(BlockLiveness)) =
    match successors with
        | [] -> false
        | successor :: rest ->
            if sortedSetContains(successor)(region) && livenessInOf(successor)(liveness)
            then true
            else anyLiveSuccessor(rest)(region)(liveness)

let recursive stepLiveness (blocks: List(IrCfgBlock)) (region: List(Int)) (facts: List(BlockFacts)) (liveness: List(BlockLiveness)) (remaining: List(Int)) =
    match remaining with
        | [] -> []
        | blockIndex :: rest ->
            match listAt(blocks)(blockIndex) with
                | Some(IrCfgBlock { blockSuccessors = successors }) ->
                    liveness
                    |> anyLiveSuccessor(successors)(region)
                    |> (given (liveOut) -> BlockLiveness(livenessBlock = blockIndex, livenessIn = hasUse(blockIndex)(facts) || liveOut, livenessOut = liveOut))
                    |> (given (entry) -> entry :: stepLiveness(blocks)(region)(facts)(liveness)(rest))
                | None -> stepLiveness(blocks)(region)(facts)(liveness)(rest)

let recursive fixLiveness (blocks: List(IrCfgBlock)) (region: List(Int)) (facts: List(BlockFacts)) (liveness: List(BlockLiveness)) =
    region
    |> stepLiveness(blocks)(region)(facts)(liveness)
    |> (given (next) ->
        if next == liveness
        then next
        else fixLiveness(blocks)(region)(facts)(next))

let recursive initialLiveness (region: List(Int)) =
    match region with
        | [] -> []
        | blockIndex :: rest -> BlockLiveness(livenessBlock = blockIndex, livenessIn = false, livenessOut = false) :: initialLiveness(rest)

let isLiveBranch (predecessor: Int) (blocks: List(IrCfgBlock)) (region: List(Int)) (liveness: List(BlockLiveness)) =
    match listAt(blocks)(predecessor) with
        | Some(IrCfgBlock { blockSuccessors = successors }) -> sortedSetContains(predecessor)(region) && length(successors) > 1 && livenessOutOf(predecessor)(liveness)
        | None -> false

let recursive hasLiveBranchPredecessor (predecessors: List(Int)) (blocks: List(IrCfgBlock)) (region: List(Int)) (liveness: List(BlockLiveness)) =
    match predecessors with
        | [] -> false
        | predecessor :: rest -> isLiveBranch(predecessor)(blocks)(region)(liveness) || hasLiveBranchPredecessor(rest)(blocks)(region)(liveness)

let recursive allPredecessorsLiveOut (predecessors: List(Int)) (region: List(Int)) (liveness: List(BlockLiveness)) =
    match predecessors with
        | [] -> true
        | predecessor :: rest -> sortedSetContains(predecessor)(region) && livenessOutOf(predecessor)(liveness) && allPredecessorsLiveOut(rest)(region)(liveness)

let retargetSwitchCase (joinLabel: Str) (edgeLabel: Str) (switchCase: IrSwitchCase) =
    if switchCase.label == joinLabel
    then switchCase with label = edgeLabel
    else switchCase

let recursive anyCaseTargets (joinLabel: Str) (cases: List(IrSwitchCase)) =
    match cases with
        | [] -> false
        | IrSwitchCase { label = label } :: rest -> label == joinLabel || anyCaseTargets(joinLabel)(rest)

// The predecessor's terminator with its explicit edge into the join rewritten to the edge block,
// `None` when the terminator reaches the join only by falling through.
let retargetedTerminator (terminator: IrInstruction) (joinLabel: Str) (edgeLabel: Str) =
    match terminator with
        | IrInstruction { instruction = Jump(target) } ->
            if target == joinLabel
            then Some((terminator with instruction = Jump(edgeLabel)))
            else None
        | IrInstruction { instruction = JumpIfFalse(condition, target) } ->
            if target == joinLabel
            then Some((terminator with instruction = JumpIfFalse(condition)(edgeLabel)))
            else None
        | IrInstruction { instruction = SwitchTag(tag, cases, defaultLabel) } ->
            if defaultLabel == joinLabel || anyCaseTargets(joinLabel)(cases)
            then
                Some((terminator with instruction = SwitchTag(tag)(map(retargetSwitchCase(joinLabel)(edgeLabel))(cases))(if defaultLabel == joinLabel
                then edgeLabel
                else defaultLabel)))
            else None
        | _ -> None

let fallsThrough (terminator: IrInstruction) =
    match terminator with
        | IrInstruction { instruction = Jump(_target) } -> false
        | IrInstruction { instruction = SwitchTag(_tag, _cases, _defaultLabel) } -> false
        | IrInstruction { instruction = Return(_result) } -> false
        | _ -> true

// A join that some predecessor reaches without the owner (a branch whose own path already
// released it, or a block outside the region) cannot drop at its entry: the drop would run twice
// on that path. The live branch's edge gets its own block instead, as `(insertions, retargets)`:
// its explicit target is rewritten to a fresh label whose block, appended after the function's
// last instruction, drops and jumps on to the join; a fallthrough edge drops right before the
// join's label, which only that predecessor reaches.
let splitLiveEdge (instructions: List(IrInstruction)) (blocks: List(IrCfgBlock)) (predecessor: Int) (joinIndex: Int) (drop: IrInstruction) (label: Str) (slot: Int) =
    match (listAt(blocks)(predecessor), listAt(blocks)(joinIndex)) with
        | (Some(IrCfgBlock { blockEnd = predecessorEnd }), Some(IrCfgBlock { blockStart = joinStart })) ->
            match (listAt(instructions)(joinStart), listAt(instructions)(predecessorEnd - 1)) with
                | (Some(IrInstruction { instruction = Label(joinLabel) }), Some(terminator)) ->
                    let edgeLabel = label + "_rc_edge_" + Ashes.Text.fromInt(slot) + "_" + Ashes.Text.fromInt(predecessor)
                    in
                        let count = length(instructions)
                        in
                            let fallthroughDrop =
                                if predecessor + 1 == joinIndex && fallsThrough(terminator)
                                then [(joinStart, drop)]
                                else []
                            in
                                match retargetedTerminator(terminator)(joinLabel)(edgeLabel) with
                                    | Some(retargeted) -> (append([(count, IrInstruction(instruction = Label(edgeLabel), location = None)), (count, drop), (count, IrInstruction(instruction = Jump(joinLabel), location = None))])(fallthroughDrop), [(predecessorEnd - 1, retargeted)])
                                    | None -> (fallthroughDrop, [])
                | (_, _) -> ([(joinStart, drop)], [])
        | _ -> ([], [])

let recursive splitLiveEdges (instructions: List(IrInstruction)) (blocks: List(IrCfgBlock)) (region: List(Int)) (liveness: List(BlockLiveness)) (predecessors: List(Int)) (joinIndex: Int) (drop: IrInstruction) (label: Str) (slot: Int) =
    match predecessors with
        | [] -> ([], [])
        | predecessor :: rest ->
            match (splitLiveEdges(instructions)(blocks)(region)(liveness)(rest)(joinIndex)(drop)(label)(slot), isLiveBranch(predecessor)(blocks)(region)(liveness)) with
                | ((restInsertions, restRetargets), true) ->
                    match splitLiveEdge(instructions)(blocks)(predecessor)(joinIndex)(drop)(label)(slot) with
                        | (edgeInsertions, edgeRetargets) -> (append(edgeInsertions)(restInsertions), append(edgeRetargets)(restRetargets))
                | (restSplit, false) -> restSplit

let isArenaCopyOut (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = CopyOutArena(_dest, _src, _size, _managed, _purpose, _semanticType) } -> true
        | IrInstruction { instruction = CopyOutArenaToSpace(_dest, _src, _size) } -> true
        | IrInstruction { instruction = CopyOutList(_dest, _src, _headCopy, _managed, _purpose) } -> true
        | IrInstruction { instruction = CopyOutClosure(_dest, _src, _managed, _purpose) } -> true
        | _ -> false

// The drop lands right after the last use, or after the reclaim that follows an arena copy-out.
// A returned alias (an escaping cell or call result that embeds the owner) is a use whose "after"
// is unreachable; the drop goes before the return, where lowering's escape handling has already
// retained or copied what the result keeps.
let lifetimeInsertionIndex (instructions: List(IrInstruction)) (lastUse: Int) =
    match (listAt(instructions)(lastUse), listAt(instructions)(lastUse + 1)) with
        | (Some(IrInstruction { instruction = Return(_result) }), _) -> lastUse
        | (Some(last), Some(IrInstruction { instruction = ReclaimArenaChunks(_saved, _preRestore, _loop) })) ->
            if isArenaCopyOut(last)
            then lastUse + 2
            else lastUse + 1
        | _ -> lastUse + 1

let recursive lastOf (items: List(Int)) =
    match items with
        | [] -> -1
        | single :: [] -> single
        | _ :: rest -> lastOf(rest)

let placedDrop (anchor: OwnerAnchor) (slot: Int) (region: OwnerRegion) =
    IrInstruction(
        instruction = RcDrop(region.regionDefinitionTemp)(anchor.anchorTypeName)(slot)(anchor.anchorRuntimeManaged)(anchor.anchorMayBeEmpty)(anchor.anchorStructuralDropper),
        location = anchor.anchorLocation
    )

// The compensating dups one owner load needs after it within its block: a record-field store of
// an alias, and a closure call handing an alias on while the owner stays live afterwards.
let recursive callDupsAfterLoad (indexed: List((Int, IrInstruction))) (aliases: List(Int)) (keepsLiveAfter: Bool) (anchor: OwnerAnchor) (tempCount: Int) (insertions: List((Int, IrInstruction))) =
    match indexed with
        | [] -> (insertions, tempCount)
        | (index, IrInstruction { instruction = kind, location = location }) :: rest ->
            match kind with
                | Borrow(target, source) ->
                    if sortedSetContains(source)(aliases)
                    then
                        callDupsAfterLoad(rest)(sortedSetInsert(target)(aliases))(keepsLiveAfter)(anchor)(tempCount)(insertions)
                    else callDupsAfterLoad(rest)(aliases)(keepsLiveAfter)(anchor)(tempCount)(insertions)
                | SetAdtField(_ptr, _fieldIndex, source, _tagless) ->
                    if sortedSetContains(source)(aliases)
                    then
                        [(index, IrInstruction(instruction = RcDup(tempCount)(source)(anchor.anchorRuntimeManaged)(anchor.anchorMayBeEmpty), location = location))]
                        |> append(insertions)
                        |> callDupsAfterLoad(rest)(aliases)(keepsLiveAfter)(anchor)(tempCount + 1)
                    else callDupsAfterLoad(rest)(aliases)(keepsLiveAfter)(anchor)(tempCount)(insertions)
                | CallClosure(_target, _closure, argument, _flag) ->
                    if sortedSetContains(argument)(aliases) && keepsLiveAfter
                    then (append(insertions)([(index, IrInstruction(instruction = RcDup(tempCount)(argument)(anchor.anchorRuntimeManaged)(false), location = location))]), tempCount + 1)
                    else callDupsAfterLoad(rest)(aliases)(keepsLiveAfter)(anchor)(tempCount)(insertions)
                | _ -> callDupsAfterLoad(rest)(aliases)(keepsLiveAfter)(anchor)(tempCount)(insertions)

let recursive callDups (instructions: List(IrInstruction)) (blockEnd: Int) (loads: List(Int)) (liveOut: Bool) (anchor: OwnerAnchor) (tempCount: Int) (insertions: List((Int, IrInstruction))) =
    match loads with
        | [] -> (insertions, tempCount)
        | loadIndex :: rest ->
            match listAt(instructions)(loadIndex) with
                | Some(IrInstruction { instruction = LoadLocal(sourceTemp, _slot) }) ->
                    match callDupsAfterLoad(indexedRange(instructions)(0)(loadIndex + 1)(blockEnd))([sourceTemp])(length(rest) > 0 || liveOut)(anchor)(tempCount)(insertions) with
                        | (nextInsertions, nextTempCount) -> callDups(instructions)(blockEnd)(rest)(liveOut)(anchor)(nextTempCount)(nextInsertions)
                | _ -> callDups(instructions)(blockEnd)(rest)(liveOut)(anchor)(tempCount)(insertions)

let recursive collectInsertions (instructions: List(IrInstruction)) (blocks: List(IrCfgBlock)) (region: List(Int)) (definitionBlock: Int) (owner: OwnerRegion) (slot: Int) (anchor: OwnerAnchor) (label: Str) (facts: List(BlockFacts)) (liveness: List(BlockLiveness)) (remaining: List(Int)) (tempCount: Int) (insertions: List((Int, IrInstruction))) (retargets: List((Int, IrInstruction))) =
    match remaining with
        | [] -> (insertions, retargets, tempCount)
        | blockIndex :: rest ->
            match (listAt(blocks)(blockIndex), factsOf(blockIndex)(facts), livenessOf(blockIndex)(liveness)) with
                | (Some(IrCfgBlock { blockStart = start, blockEnd = end, blockPredecessors = predecessors }), BlockFacts { factsLoads = loads, factsUses = uses }, BlockLiveness { livenessIn = liveIn, livenessOut = liveOut }) ->
                    ((given (dropsAndRetargets) ->
                        match dropsAndRetargets with
                            | (drops, edgeRetargets) ->
                                match drops
                                |> append(insertions)
                                |> callDups(instructions)(end)(loads)(liveOut)(anchor)(tempCount) with
                                    | (nextInsertions, nextTempCount) ->
                                        edgeRetargets
                                        |> append(retargets)
                                        |> collectInsertions(instructions)(blocks)(region)(definitionBlock)(owner)(slot)(anchor)(label)(facts)(liveness)(rest)(nextTempCount)(nextInsertions)))(if length(uses) > 0 && liveOut == false
                    then
                        ([(uses
                        |> lastOf
                        |> lifetimeInsertionIndex(instructions), placedDrop(anchor)(slot)(owner))], [])
                    else
                        if liveIn == false && blockIndex == definitionBlock
                        then ([(owner.regionDefinitionIndex + 1, placedDrop(anchor)(slot)(owner))], [])
                        else
                            if liveIn == false && hasLiveBranchPredecessor(predecessors)(blocks)(region)(liveness)
                            then
                                if allPredecessorsLiveOut(predecessors)(region)(liveness)
                                then
                                    match listAt(instructions)(start) with
                                        | Some(IrInstruction { instruction = Label(_name) }) -> ([(start + 1, placedDrop(anchor)(slot)(owner))], [])
                                        | _ -> ([(start, placedDrop(anchor)(slot)(owner))], [])
                                else
                                    splitLiveEdges(instructions)(blocks)(region)(liveness)(predecessors)(blockIndex)(placedDrop(anchor)(slot)(owner))(label)(slot)
                            else ([], []))
                | _ -> collectInsertions(instructions)(blocks)(region)(definitionBlock)(owner)(slot)(anchor)(label)(facts)(liveness)(rest)(tempCount)(insertions)(retargets)

let recursive distinctIndicesDescending (insertions: List((Int, IrInstruction))) (acc: List(Int)) =
    match insertions with
        | [] -> reverse(acc)
        | (index, _instruction) :: rest ->
            acc
            |> sortedSetInsert(index)
            |> distinctIndicesDescending(rest)

let recursive applyInsertions (indices: List(Int)) (insertions: List((Int, IrInstruction))) (instructions: List(IrInstruction)) =
    match indices with
        | [] -> instructions
        | index :: rest ->
            insertions
            |> filter(given (entry) ->
                match entry with
                    | (candidate, _instruction) -> candidate == index)
            |> map(given (entry) ->
                match entry with
                    | (_candidate, instruction) -> instruction)
            |> (given (added) ->
                instructions
                |> insertAllAt(index)(added)
                |> applyInsertions(rest)(insertions))

let boundaryIndexWithin (count: Int) (owner: OwnerRegion) =
    if owner.regionBoundaryIndex < count - 1
    then owner.regionBoundaryIndex
    else
        if count - 1 > 0
        then count - 1
        else 0

let recursive replaceAt (items: List(IrInstruction)) (index: Int) (replacement: IrInstruction) =
    match items with
        | [] -> []
        | head :: rest ->
            if index == 0
            then replacement :: rest
            else head :: replaceAt(rest)(index - 1)(replacement)

// Rewrites each retargeted terminator in place; indices are unchanged, so this runs before the
// insertions.
let recursive applyRetargets (retargets: List((Int, IrInstruction))) (instructions: List(IrInstruction)) =
    match retargets with
        | [] -> instructions
        | (index, replacement) :: rest ->
            replacement
            |> replaceAt(instructions)(index)
            |> applyRetargets(rest)

let placeOwnerInRegion (instructions: List(IrInstruction)) (slot: Int) (owner: OwnerRegion) (anchor: OwnerAnchor) (label: Str) (tempCount: Int) (blocks: List(IrCfgBlock)) (definitionBlock: Int) (region: List(Int)) =
    slot
    |> collectOwnerAliases(blocks)(regionInstructions(instructions)(blocks)(region))
    |> blockFactsFor(instructions)(blocks)(region)(slot)
    |> (given (facts) ->
        match region
        |> initialLiveness
        |> fixLiveness(blocks)(region)(facts)
        |> (given (liveness) -> collectInsertions(instructions)(blocks)(region)(definitionBlock)(owner)(slot)(anchor)(label)(facts)(liveness)(region)(tempCount)([])([])) with
            | (insertions, retargets, nextTempCount) ->
                PlacedInstructions(placedInstructions = instructions
                |> applyRetargets(retargets)
                |> applyInsertions(distinctIndicesDescending(insertions)([]))(insertions), placedTempCount = nextTempCount))

let placeOwnerBetween (instructions: List(IrInstruction)) (slot: Int) (owner: OwnerRegion) (anchor: OwnerAnchor) (label: Str) (tempCount: Int) (blocks: List(IrCfgBlock)) (definitionBlock: Int) (boundaryBlock: Int) =
    []
    |> reachableBeforeBoundary(blocks)(computeDominators(blocks))(definitionBlock)(boundaryBlock)([definitionBlock])
    |> (given (region) ->
        if length(region) == 0
        then PlacedInstructions(placedInstructions = instructions, placedTempCount = tempCount)
        else placeOwnerInRegion(instructions)(slot)(owner)(anchor)(label)(tempCount)(blocks)(definitionBlock)(region))

let placeOwnerRegion (instructions: List(IrInstruction)) (slot: Int) (owner: OwnerRegion) (anchor: OwnerAnchor) (label: Str) (tempCount: Int) =
    instructions
    |> buildCfgBlocks
    |> (given (blocks) ->
        match (findCfgBlock(blocks)(owner.regionDefinitionIndex), owner
        |> boundaryIndexWithin(length(instructions))
        |> findCfgBlock(blocks)) with
            | (definitionBlock, boundaryBlock) ->
                if definitionBlock < 0 || boundaryBlock < 0
                then PlacedInstructions(placedInstructions = instructions, placedTempCount = tempCount)
                else placeOwnerBetween(instructions)(slot)(owner)(anchor)(label)(tempCount)(blocks)(definitionBlock)(boundaryBlock))

// Removes the lexical anchor (and the owner load feeding it) and places the owner's drop.
let placeOwner (label: Str) (slot: Int) (anchorIndex: Int) (placed: PlacedInstructions) =
    match placed with
        | PlacedInstructions { placedInstructions = instructions, placedTempCount = tempCount } ->
            match (listAt(instructions)(anchorIndex), ownerDefinition(instructions)(slot)(0)) with
                | (Some(IrInstruction { instruction = RcDrop(source, typeName, _slot, runtimeManaged, mayBeEmpty, dropper), location = location }), Some((definitionIndex, definitionTemp))) ->
                    anchorIndex
                    |> removeAt(instructions)
                    |> (given (withoutAnchor) ->
                        match listAt(withoutAnchor)(anchorIndex - 1) with
                            | Some(IrInstruction { instruction = LoadLocal(target, loadSlot) }) ->
                                if anchorIndex > 0 && loadSlot == slot && target == source
                                then (removeAt(withoutAnchor)(anchorIndex - 1), anchorIndex - 1)
                                else (withoutAnchor, anchorIndex)
                            | _ -> (withoutAnchor, anchorIndex))
                    |> (given (removed) ->
                        match removed with
                            | (remaining, boundary) -> placeOwnerRegion(remaining)(slot)(OwnerRegion(regionDefinitionIndex = definitionIndex, regionBoundaryIndex = boundary, regionDefinitionTemp = definitionTemp))(OwnerAnchor(anchorTypeName = typeName, anchorRuntimeManaged = runtimeManaged, anchorMayBeEmpty = mayBeEmpty, anchorStructuralDropper = dropper, anchorLocation = location))(label)(tempCount))
                | _ -> placed

let recursive placeOwnerSlots (label: Str) (slots: List(Int)) (placed: PlacedInstructions) =
    match slots with
        | [] -> placed
        | slot :: rest ->
            placeOwnerSlots(label)(rest)(match anchorIndices(placed.placedInstructions)(slot)(0) with
                | anchorIndex :: [] -> placeOwner(label)(slot)(anchorIndex)(placed)
                | _ -> placed)

// Places the lifetimes of one function body; `label` names the function, prefixing the labels of
// the drop blocks that split a live branch edge.
let placeInstructionLifetimesIn (label: Str) (instructions: List(IrInstruction)) (tempCount: Int) =
    placeOwnerSlots(label)(ownerSlots(instructions)([]))(PlacedInstructions(placedInstructions = instructions, placedTempCount = tempCount))

let placeInstructionLifetimes (instructions: List(IrInstruction)) (tempCount: Int) = placeInstructionLifetimesIn("")(instructions)(tempCount)

let placeFunctionLifetimes (function_: IrFunction) =
    if function_.lifetimesPlaced
    then function_
    else
        match placeInstructionLifetimesIn(function_.label)(function_.instructions)(function_.tempCount) with
            | PlacedInstructions { placedInstructions = instructions, placedTempCount = tempCount } -> function_ with instructions = instructions, tempCount = tempCount, lifetimesPlaced = true

let placeLifetimes (program: IrProgram) = program with entryFunction = placeFunctionLifetimes(program.entryFunction), functions = map(placeFunctionLifetimes)(program.functions)
