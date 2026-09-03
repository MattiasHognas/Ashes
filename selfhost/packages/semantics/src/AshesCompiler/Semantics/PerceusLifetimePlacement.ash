// Moves ordinary-value lifetime markers from lexical scope exits to control-flow precise last-use
// points, stage 0's `PerceusLifetimePlacement`. Lowering anchors an owned binding's release at its
// scope exit as `LoadLocal` of the owner slot followed by `RcDrop` naming that slot as `OwnerSlot`.
// For every slot with exactly one such anchor this pass removes the anchor (and its load), takes
// the region of blocks reachable from the binding's definition, dominated by it, and before the
// anchor, computes where the owner (through every alias: `Borrow`, an alias-holding slot or
// closure environment, a partial application) is still live, and re-inserts the drop, now naming
// the definition's source temp, after the last use in each block where the value dies, at the
// definition when it is never used, and at the entry of a block reached from a live branch. A
// `CallClosure` that hands an alias to a callee while the owner stays live afterwards, and a
// record-field store of an alias, get a compensating `RcDup`. Resource cleanup
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

type AliasState =
    | aliasTemps: List(Int)
    | aliasSlots: List(Int)
    | aliasEnvironments: List(Int)

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

// Closure temps applied by `CallClosure` and environment temps entered by `CallKnown`: the
// results whose own application marks them as partial-application stages.
let recursive appliedClosureTemps (indexed: List((Int, IrInstruction))) (acc: List(Int)) =
    match indexed with
        | [] -> acc
        | (_index, IrInstruction { instruction = CallClosure(_target, closureTemp, _argument, _flag) }) :: rest ->
            acc
            |> sortedSetInsert(closureTemp)
            |> appliedClosureTemps(rest)
        | (_index, IrInstruction { instruction = CallKnown(_target, _label, environmentTemp, _argument, _flag, _borrowed) }) :: rest ->
            acc
            |> sortedSetInsert(environmentTemp)
            |> appliedClosureTemps(rest)
        | _ :: rest -> appliedClosureTemps(rest)(acc)

let aliasTempsGrew (before: AliasState) (after: AliasState) = length(after.aliasTemps) > length(before.aliasTemps) || length(after.aliasSlots) > length(before.aliasSlots) || length(after.aliasEnvironments) > length(before.aliasEnvironments)

let propagateAlias (partials: List(Int)) (state: AliasState) (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = Borrow(target, source) } ->
            if sortedSetContains(source)(state.aliasTemps)
            then state with aliasTemps = sortedSetInsert(target)(state.aliasTemps)
            else state
        | IrInstruction { instruction = StoreLocal(slot, source) } ->
            if sortedSetContains(source)(state.aliasTemps)
            then state with aliasSlots = sortedSetInsert(slot)(state.aliasSlots)
            else state
        | IrInstruction { instruction = LoadLocal(target, slot) } ->
            if sortedSetContains(slot)(state.aliasSlots)
            then state with aliasTemps = sortedSetInsert(target)(state.aliasTemps)
            else state
        | IrInstruction { instruction = StoreMemOffset(basePtr, _offset, source) } ->
            if sortedSetContains(source)(state.aliasTemps)
            then state with aliasEnvironments = sortedSetInsert(basePtr)(state.aliasEnvironments)
            else state
        | IrInstruction { instruction = MakeClosure(target, _label, environmentPtr, _size, _managed, _returnsManaged, _acceptsManaged) } ->
            if sortedSetContains(environmentPtr)(state.aliasEnvironments)
            then state with aliasTemps = sortedSetInsert(target)(state.aliasTemps)
            else state
        | IrInstruction { instruction = MakeClosureStack(target, _label, environmentPtr, _size, _returnsManaged, _acceptsManaged) } ->
            if sortedSetContains(environmentPtr)(state.aliasEnvironments)
            then state with aliasTemps = sortedSetInsert(target)(state.aliasTemps)
            else state
        | IrInstruction { instruction = CallClosure(target, closureTemp, argument, _flag) } ->
            if sortedSetContains(target)(partials) && (sortedSetContains(argument)(state.aliasTemps) || sortedSetContains(closureTemp)(state.aliasTemps))
            then state with aliasTemps = sortedSetInsert(target)(state.aliasTemps)
            else state
        | IrInstruction { instruction = CallKnown(target, _label, environmentTemp, argument, _flag, _borrowed) } ->
            if sortedSetContains(target)(partials) && (sortedSetContains(argument)(state.aliasTemps) || sortedSetContains(environmentTemp)(state.aliasEnvironments))
            then state with aliasTemps = sortedSetInsert(target)(state.aliasTemps)
            else state
        | _ -> state

let recursive propagateAliases (partials: List(Int)) (indexed: List((Int, IrInstruction))) (state: AliasState) =
    match indexed with
        | [] -> state
        | (_index, instruction) :: rest ->
            instruction
            |> propagateAlias(partials)(state)
            |> propagateAliases(partials)(rest)

let recursive aliasFixpoint (partials: List(Int)) (indexed: List((Int, IrInstruction))) (state: AliasState) =
    state
    |> propagateAliases(partials)(indexed)
    |> (given (next) ->
        if aliasTempsGrew(state)(next)
        then aliasFixpoint(partials)(indexed)(next)
        else next)

// Every temp aliasing the owner within the region, to a fixpoint.
let collectOwnerAliases (indexed: List((Int, IrInstruction))) (slot: Int) =
    AliasState(aliasTemps = ownerLoadTargets(indexed)(slot)([]), aliasSlots = [], aliasEnvironments = [])
    |> aliasFixpoint(appliedClosureTemps(indexed)([]))(indexed)
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

let recursive hasLiveBranchPredecessor (predecessors: List(Int)) (blocks: List(IrCfgBlock)) (region: List(Int)) (liveness: List(BlockLiveness)) =
    match predecessors with
        | [] -> false
        | predecessor :: rest ->
            match listAt(blocks)(predecessor) with
                | Some(IrCfgBlock { blockSuccessors = successors }) ->
                    if sortedSetContains(predecessor)(region) && length(successors) > 1 && livenessOutOf(predecessor)(liveness)
                    then true
                    else hasLiveBranchPredecessor(rest)(blocks)(region)(liveness)
                | None -> hasLiveBranchPredecessor(rest)(blocks)(region)(liveness)

let isArenaCopyOut (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = CopyOutArena(_dest, _src, _size, _managed, _purpose, _semanticType) } -> true
        | IrInstruction { instruction = CopyOutArenaToSpace(_dest, _src, _size) } -> true
        | IrInstruction { instruction = CopyOutList(_dest, _src, _headCopy, _managed, _purpose) } -> true
        | IrInstruction { instruction = CopyOutClosure(_dest, _src, _managed, _purpose) } -> true
        | _ -> false

// The drop lands right after the last use, or after the reclaim that follows an arena copy-out.
let lifetimeInsertionIndex (instructions: List(IrInstruction)) (lastUse: Int) =
    match (listAt(instructions)(lastUse), listAt(instructions)(lastUse + 1)) with
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

let recursive collectInsertions (instructions: List(IrInstruction)) (blocks: List(IrCfgBlock)) (region: List(Int)) (definitionBlock: Int) (owner: OwnerRegion) (slot: Int) (anchor: OwnerAnchor) (facts: List(BlockFacts)) (liveness: List(BlockLiveness)) (remaining: List(Int)) (tempCount: Int) (insertions: List((Int, IrInstruction))) =
    match remaining with
        | [] -> (insertions, tempCount)
        | blockIndex :: rest ->
            match (listAt(blocks)(blockIndex), factsOf(blockIndex)(facts), livenessOf(blockIndex)(liveness)) with
                | (Some(IrCfgBlock { blockStart = start, blockEnd = end, blockPredecessors = predecessors }), BlockFacts { factsLoads = loads, factsUses = uses }, BlockLiveness { livenessIn = liveIn, livenessOut = liveOut }) ->
                    ((given (drops) ->
                        match drops
                        |> append(insertions)
                        |> callDups(instructions)(end)(loads)(liveOut)(anchor)(tempCount) with
                            | (nextInsertions, nextTempCount) -> collectInsertions(instructions)(blocks)(region)(definitionBlock)(owner)(slot)(anchor)(facts)(liveness)(rest)(nextTempCount)(nextInsertions)))(if length(uses) > 0 && liveOut == false
                    then
                        [(uses
                        |> lastOf
                        |> lifetimeInsertionIndex(instructions), placedDrop(anchor)(slot)(owner))]
                    else
                        if liveIn == false && blockIndex == definitionBlock
                        then [(owner.regionDefinitionIndex + 1, placedDrop(anchor)(slot)(owner))]
                        else
                            if liveIn == false && hasLiveBranchPredecessor(predecessors)(blocks)(region)(liveness)
                            then
                                match listAt(instructions)(start) with
                                    | Some(IrInstruction { instruction = Label(_name) }) -> [(start + 1, placedDrop(anchor)(slot)(owner))]
                                    | _ -> [(start, placedDrop(anchor)(slot)(owner))]
                            else [])
                | _ -> collectInsertions(instructions)(blocks)(region)(definitionBlock)(owner)(slot)(anchor)(facts)(liveness)(rest)(tempCount)(insertions)

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

let placeOwnerInRegion (instructions: List(IrInstruction)) (slot: Int) (owner: OwnerRegion) (anchor: OwnerAnchor) (tempCount: Int) (blocks: List(IrCfgBlock)) (definitionBlock: Int) (region: List(Int)) =
    slot
    |> collectOwnerAliases(regionInstructions(instructions)(blocks)(region))
    |> blockFactsFor(instructions)(blocks)(region)(slot)
    |> (given (facts) ->
        match region
        |> initialLiveness
        |> fixLiveness(blocks)(region)(facts)
        |> (given (liveness) -> collectInsertions(instructions)(blocks)(region)(definitionBlock)(owner)(slot)(anchor)(facts)(liveness)(region)(tempCount)([])) with
            | (insertions, nextTempCount) ->
                PlacedInstructions(placedInstructions = applyInsertions(distinctIndicesDescending(insertions)([]))(insertions)(instructions), placedTempCount = nextTempCount))

let placeOwnerBetween (instructions: List(IrInstruction)) (slot: Int) (owner: OwnerRegion) (anchor: OwnerAnchor) (tempCount: Int) (blocks: List(IrCfgBlock)) (definitionBlock: Int) (boundaryBlock: Int) =
    []
    |> reachableBeforeBoundary(blocks)(computeDominators(blocks))(definitionBlock)(boundaryBlock)([definitionBlock])
    |> (given (region) ->
        if length(region) == 0
        then PlacedInstructions(placedInstructions = instructions, placedTempCount = tempCount)
        else placeOwnerInRegion(instructions)(slot)(owner)(anchor)(tempCount)(blocks)(definitionBlock)(region))

let placeOwnerRegion (instructions: List(IrInstruction)) (slot: Int) (owner: OwnerRegion) (anchor: OwnerAnchor) (tempCount: Int) =
    instructions
    |> buildCfgBlocks
    |> (given (blocks) ->
        match (findCfgBlock(blocks)(owner.regionDefinitionIndex), owner
        |> boundaryIndexWithin(length(instructions))
        |> findCfgBlock(blocks)) with
            | (definitionBlock, boundaryBlock) ->
                if definitionBlock < 0 || boundaryBlock < 0
                then PlacedInstructions(placedInstructions = instructions, placedTempCount = tempCount)
                else placeOwnerBetween(instructions)(slot)(owner)(anchor)(tempCount)(blocks)(definitionBlock)(boundaryBlock))

// Removes the lexical anchor (and the owner load feeding it) and places the owner's drop.
let placeOwner (slot: Int) (anchorIndex: Int) (placed: PlacedInstructions) =
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
                            | (remaining, boundary) -> placeOwnerRegion(remaining)(slot)(OwnerRegion(regionDefinitionIndex = definitionIndex, regionBoundaryIndex = boundary, regionDefinitionTemp = definitionTemp))(OwnerAnchor(anchorTypeName = typeName, anchorRuntimeManaged = runtimeManaged, anchorMayBeEmpty = mayBeEmpty, anchorStructuralDropper = dropper, anchorLocation = location))(tempCount))
                | _ -> placed

let recursive placeOwnerSlots (slots: List(Int)) (placed: PlacedInstructions) =
    match slots with
        | [] -> placed
        | slot :: rest ->
            placeOwnerSlots(rest)(match anchorIndices(placed.placedInstructions)(slot)(0) with
                | anchorIndex :: [] -> placeOwner(slot)(anchorIndex)(placed)
                | _ -> placed)

let placeInstructionLifetimes (instructions: List(IrInstruction)) (tempCount: Int) =
    placeOwnerSlots(ownerSlots(instructions)([]))(PlacedInstructions(placedInstructions = instructions, placedTempCount = tempCount))

let placeFunctionLifetimes (function_: IrFunction) =
    if function_.lifetimesPlaced
    then function_
    else
        match placeInstructionLifetimes(function_.instructions)(function_.tempCount) with
            | PlacedInstructions { placedInstructions = instructions, placedTempCount = tempCount } -> function_ with instructions = instructions, tempCount = tempCount, lifetimesPlaced = true

let placeLifetimes (program: IrProgram) = program with entryFunction = placeFunctionLifetimes(program.entryFunction), functions = map(placeFunctionLifetimes)(program.functions)
