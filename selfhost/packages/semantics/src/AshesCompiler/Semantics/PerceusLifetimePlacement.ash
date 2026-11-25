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
// the owner stays live afterwards, and a record-field store of an alias into a runtime-managed
// cell, get a compensating `RcDup`; a store into an arena- or stack-allocated cell borrows
// instead (see `arenaAdtCellTarget`), since such a cell never releases the fields stored into it.
// Resource cleanup
// (`CleanupResource`) is deliberately outside this pass. A function whose `lifetimesPlaced` flag
// is already set is returned unchanged.
import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.Map
import Ashes.Collection.Map.MapTree
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

// What is known of the function's block graph while its owners are placed one after another.
// graphDominators carries the immediate dominators from the first owner that needed them to every
// later owner, with the index answering dominance out of them: the table is recomputed only when
// the block count has changed, as stage 0's PlaceOwner does. graphBlocks carries the blocks of the
// placed instructions the same way, with per block whether it opens with a label and ends in a
// terminator (graphKinds) and with the edges by block index (graphEdges): an owner's edits mostly
// move instruction indices inside the blocks, so the spans are shifted and the edges kept.
// graphBlocks is empty when the edits may have changed the blocks themselves (an emptied block, a
// drop opening a block of its own, a split edge), and the next owner builds the graph from the
// instructions.
type OwnerGraph =
    | graphBlocks: List(IrCfgBlock)
    | graphKinds: List((Bool, Bool))
    | graphEdges: BlockEdges
    | graphDominators: List(Int)
    | graphDominance: DominanceIndex

type PlacedInstructions =
    | placedInstructions: List(IrInstruction)
    | placedTempCount: Int
    | placedGraph: OwnerGraph
    | placedUses: List(List(Int))
    | placedCopySources: List(Int)

let graphSuccessors (graph: OwnerGraph) =
    (let edges = graph.graphEdges
    in edges.successorsOfBlock)

let graphPredecessors (graph: OwnerGraph) =
    (let edges = graph.graphEdges
    in edges.predecessorsOfBlock)

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
            let slot = anchorSlot(head)
            in
                if slot >= 0 && containsInt(slot)(acc) == false
                then ownerSlots(rest)(slot :: acc)
                else ownerSlots(rest)(acc)

// The target of an arena- or stack-allocated ADT cell, stage 0's `CollectArenaAdtCells`: such a
// cell never releases the fields stored into it (only a runtime-managed cell owns its field's
// reference and releases it with its structural dropper), so a field store into one embeds an
// owner alias without a reference of its own — the owner stays live while the cell does and the
// store gets no compensating dup.
let arenaAdtCellTarget (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = AllocAdt(target, _tag, _fieldCount, false, _tagless) } -> target
        | IrInstruction { instruction = AllocAdtStack(target, _tag, _fieldCount, _tagless) } -> target
        | _ -> -1

// An earlier owner's split edge (`<function>_rc_edge_<slot>_<block>`) sits after the function's
// last block but stands on an edge between two blocks; one that rejoins a block before the
// boundary is part of the region, so the owner is released on that edge too.
let isSplitEdgeWithin (instructions: List(IrInstruction)) (blocks: List(IrCfgBlock)) (label: Str) (boundary: Int) (blockIndex: Int) =
    match listAt(blocks)(blockIndex) with
        | Some(IrCfgBlock { blockStart = start, blockSuccessors = successor :: [] }) ->
            match listAt(instructions)(start) with
                | Some(IrInstruction { instruction = Label(name) }) -> successor <= boundary && Ashes.Text.startsWith(name)(label + "_rc_edge_")
                | _ -> false
        | _ -> false

// The stores of an alias into one slot: whether they sit in more than one block, and otherwise the
// span of their block and the first of them.
type SlotAliasStores =
    | storesInSeveralBlocks: Bool
    | storesBlockStart: Int
    | storesBlockEnd: Int
    | storesFirstIndex: Int

// The alias sets of one owner as maps, with their sizes kept beside them so a fixpoint round can
// tell whether it added anything. `aliasStoreIndices` holds the index of every `StoreLocal` of an
// alias seen so far (one instruction is one store), and `aliasSlotStores` what a load of a slot
// needs to know of the stores into it.
type AliasSets =
    | aliasSet: MapTree(Int, Bool)
    | aliasSetSize: Int
    // The aliases that are views naming the owner's own bytes, and the pieces read back out of the
    // tuple a view-producing instruction allocates. Only these follow a field read, so an ordinary
    // aggregate holding the owner does not lengthen the owner's liveness through every read of it.
    | viewAliasSet: MapTree(Int, Bool)
    | viewAliasSetSize: Int
    // The slots a view alias was stored into: a load of one reads the view back, so it is a view
    // alias too, and the field reads of what it holds follow it (a `let` binding the result of an
    // `uncons`, matched later).
    | viewSlotSet: MapTree(Int, Bool)
    | viewSlotCount: Int
    | aliasStoreIndices: MapTree(Int, Bool)
    | aliasStoreCount: Int
    | aliasSlotStores: MapTree(Int, SlotAliasStores)

let inTempSet (temp: Int) (set: MapTree(Int, Bool)) =
    match Ashes.Collection.Map.getInt(temp)(set) with
        | Some(_listed) -> true
        | None -> false

let withAliasTemp (target: Int) (sets: AliasSets) =
    if inTempSet(target)(sets.aliasSet)
    then sets
    else sets with aliasSet = Ashes.Collection.Map.setInt(target)(true)(sets.aliasSet), aliasSetSize = sets.aliasSetSize + 1

let withViewAliasTemp (target: Int) (sets: AliasSets) =
    (let aliased = withAliasTemp(target)(sets)
    in
        if inTempSet(target)(aliased.viewAliasSet)
        then aliased
        else aliased with viewAliasSet = Ashes.Collection.Map.setInt(target)(true)(aliased.viewAliasSet), viewAliasSetSize = aliased.viewAliasSetSize + 1)

let withViewSlot (slot: Int) (sets: AliasSets) =
    if inTempSet(slot)(sets.viewSlotSet)
    then sets
    else sets with viewSlotSet = Ashes.Collection.Map.setInt(slot)(true)(sets.viewSlotSet), viewSlotCount = sets.viewSlotCount + 1

let slotStoresWith (index: Int) (start: Int) (end: Int) (known: Maybe(SlotAliasStores)) =
    match known with
        | None -> SlotAliasStores(storesInSeveralBlocks = false, storesBlockStart = start, storesBlockEnd = end, storesFirstIndex = index)
        | Some(SlotAliasStores { storesInSeveralBlocks = several, storesBlockStart = knownStart, storesBlockEnd = knownEnd, storesFirstIndex = first }) ->
            if several || knownStart != start || knownEnd != end
            then SlotAliasStores(storesInSeveralBlocks = true, storesBlockStart = knownStart, storesBlockEnd = knownEnd, storesFirstIndex = first)
            else
                SlotAliasStores(storesInSeveralBlocks = false, storesBlockStart = knownStart, storesBlockEnd = knownEnd, storesFirstIndex = if index < first
                then index
                else first)

let withAliasStore (slot: Int) (index: Int) (start: Int) (end: Int) (sets: AliasSets) =
    if inTempSet(index)(sets.aliasStoreIndices)
    then sets
    else
        sets with aliasStoreIndices = Ashes.Collection.Map.setInt(index)(true)(sets.aliasStoreIndices), aliasStoreCount = sets.aliasStoreCount + 1, aliasSlotStores = Ashes.Collection.Map.setInt(slot)(sets.aliasSlotStores
        |> Ashes.Collection.Map.getInt(slot)
        |> slotStoresWith(index)(start)(end))(sets.aliasSlotStores)

// A load reads an alias out of its slot only past a store of one: a store later in the load's
// own block is a different value (a loop parameter's successor stored after the old value was
// read for its release walk), while a store in any other block may reach the load.
let loadSeesStoredAlias (slot: Int) (loadIndex: Int) (start: Int) (end: Int) (sets: AliasSets) =
    match Ashes.Collection.Map.getInt(slot)(sets.aliasSlotStores) with
        | None -> false
        | Some(SlotAliasStores { storesInSeveralBlocks = several, storesBlockStart = storeStart, storesBlockEnd = storeEnd, storesFirstIndex = first }) -> several || storeStart != start || storeEnd != end || first < loadIndex

// One propagation step over a single instruction of the block spanning `[start, end)`; the cases
// are `propagateAlias`'s.
let propagateAliasInto (sets: AliasSets) (index: Int) (start: Int) (end: Int) (arenaAdtCells: MapTree(Int, Bool)) (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = Borrow(target, source) } ->
            if inTempSet(source)(sets.viewAliasSet)
            then withViewAliasTemp(target)(sets)
            else
                if inTempSet(source)(sets.aliasSet)
                then withAliasTemp(target)(sets)
                else sets
        | IrInstruction { instruction = StoreLocal(slot, source) } ->
            if inTempSet(source)(sets.aliasSet)
            then
                sets
                |> withAliasStore(slot)(index)(start)(end)
                |> (given (stored: AliasSets) ->
                    if inTempSet(source)(sets.viewAliasSet)
                    then withViewSlot(slot)(stored)
                    else stored)
            else sets
        | IrInstruction { instruction = LoadLocal(target, slot) } ->
            if loadSeesStoredAlias(slot)(index)(start)(end)(sets)
            then
                if inTempSet(slot)(sets.viewSlotSet)
                then withViewAliasTemp(target)(sets)
                else withAliasTemp(target)(sets)
            else sets
        | IrInstruction { instruction = StoreMemOffset(basePtr, _offset, source) } ->
            if inTempSet(source)(sets.aliasSet)
            then withAliasTemp(basePtr)(sets)
            else sets
        | IrInstruction { instruction = SetAdtField(ptr, _fieldIndex, source, _tagless) } ->
            if inTempSet(source)(sets.aliasSet) && inTempSet(ptr)(arenaAdtCells)
            then withAliasTemp(ptr)(sets)
            else sets
        | IrInstruction { instruction = MakeClosure(target, _label, environmentPtr, _size, _managed, _returnsManaged, _acceptsManaged, _returnsOwned) } ->
            if inTempSet(environmentPtr)(sets.aliasSet)
            then withAliasTemp(target)(sets)
            else sets
        | IrInstruction { instruction = MakeClosureStack(target, _label, environmentPtr, _size, _returnsManaged, _acceptsManaged, _returnsOwned) } ->
            if inTempSet(environmentPtr)(sets.aliasSet)
            then withAliasTemp(target)(sets)
            else sets
        | IrInstruction { instruction = CallClosure(target, closureTemp, argument, _flag) } ->
            if inTempSet(argument)(sets.aliasSet) || inTempSet(closureTemp)(sets.aliasSet)
            then withAliasTemp(target)(sets)
            else sets
        | IrInstruction { instruction = CallKnown(target, _label, environmentTemp, argument, _flag, _borrowed) } ->
            if inTempSet(argument)(sets.aliasSet) || inTempSet(environmentTemp)(sets.aliasSet)
            then withAliasTemp(target)(sets)
            else sets
        | IrInstruction { instruction = TextUncons(target, textTemp, false) } ->
            if inTempSet(textTemp)(sets.aliasSet)
            then withViewAliasTemp(target)(sets)
            else sets
        | IrInstruction { instruction = TextUnconsText(target, textTemp, false) } ->
            if inTempSet(textTemp)(sets.aliasSet)
            then withViewAliasTemp(target)(sets)
            else sets
        | IrInstruction { instruction = BytesSubView(target, bytesTemp, _start, _length) } ->
            if inTempSet(bytesTemp)(sets.aliasSet)
            then withViewAliasTemp(target)(sets)
            else sets
        | IrInstruction { instruction = GetAdtField(target, ptr, _fieldIndex, _tagless) } ->
            if inTempSet(ptr)(sets.viewAliasSet)
            then withViewAliasTemp(target)(sets)
            else sets
        | IrInstruction { instruction = LoadMemOffset(target, basePtr, _offset) } ->
            if inTempSet(basePtr)(sets.viewAliasSet)
            then withViewAliasTemp(target)(sets)
            else sets
        | _ -> sets

// One pass over the region's instructions, which run block after block in the order of `spans`
// (block index, start, end): the span of the block an instruction sits in is the one the walk
// stands on.
let recursive propagateAliasesAlong (spans: List((Int, Int, Int))) (indexed: List((Int, IrInstruction, List(Int)))) (arenaAdtCells: MapTree(Int, Bool)) (sets: AliasSets) =
    match indexed with
        | [] -> sets
        | (index, instruction, _used) :: rest ->
            match spans with
                | [] ->
                    instruction
                    |> propagateAliasInto(sets)(index)(0)(0)(arenaAdtCells)
                    |> propagateAliasesAlong([])(rest)(arenaAdtCells)
                | (_block, start, end) :: restSpans ->
                    if index >= end
                    then propagateAliasesAlong(restSpans)(indexed)(arenaAdtCells)(sets)
                    else
                        instruction
                        |> propagateAliasInto(sets)(index)(start)(end)(arenaAdtCells)
                        |> propagateAliasesAlong(spans)(rest)(arenaAdtCells)

let recursive aliasSetFixpoint (spans: List((Int, Int, Int))) (indexed: List((Int, IrInstruction, List(Int)))) (arenaAdtCells: MapTree(Int, Bool)) (sets: AliasSets) =
    (let next = propagateAliasesAlong(spans)(indexed)(arenaAdtCells)(sets)
    in
        if next.aliasSetSize > sets.aliasSetSize || next.aliasStoreCount > sets.aliasStoreCount || next.viewAliasSetSize > sets.viewAliasSetSize || next.viewSlotCount > sets.viewSlotCount
        then aliasSetFixpoint(spans)(indexed)(arenaAdtCells)(next)
        else next)

let recursive ownerLoadsInto (indexed: List((Int, IrInstruction, List(Int)))) (slot: Int) (sets: AliasSets) =
    match indexed with
        | [] -> sets
        | (_index, IrInstruction { instruction = LoadLocal(target, candidate) }, _used) :: rest ->
            if candidate == slot
            then
                sets
                |> withAliasTemp(target)
                |> ownerLoadsInto(rest)(slot)
            else ownerLoadsInto(rest)(slot)(sets)
        | _ :: rest -> ownerLoadsInto(rest)(slot)(sets)

// Every temp aliasing the owner within the region, to a fixpoint.
let collectOwnerAliasSet (spans: List((Int, Int, Int))) (indexed: List((Int, IrInstruction, List(Int)))) (slot: Int) (arenaAdtCells: MapTree(Int, Bool)) =
    (let settled =
        AliasSets(aliasSet = Ashes.Collection.Map.empty, aliasSetSize = 0, viewAliasSet = Ashes.Collection.Map.empty, viewAliasSetSize = 0, viewSlotSet = Ashes.Collection.Map.empty, viewSlotCount = 0, aliasStoreIndices = Ashes.Collection.Map.empty, aliasStoreCount = 0, aliasSlotStores = Ashes.Collection.Map.empty)
        |> ownerLoadsInto(indexed)(slot)
        |> aliasSetFixpoint(spans)(indexed)(arenaAdtCells)
    in settled.aliasSet)

// The region's blocks with their spans, ascending, in one walk of the blocks.
let recursive regionSpansFrom (blocks: List(IrCfgBlock)) (index: Int) (region: List(Int)) =
    match region with
        | [] -> []
        | blockIndex :: restRegion ->
            match blocks with
                | [] -> (blockIndex, 0, 0) :: regionSpansFrom([])(index)(restRegion)
                | IrCfgBlock { blockStart = start, blockEnd = end } :: restBlocks ->
                    if index == blockIndex
                    then (blockIndex, start, end) :: regionSpansFrom(restBlocks)(index + 1)(restRegion)
                    else regionSpansFrom(restBlocks)(index + 1)(region)

// The instructions of the region's blocks with their indices and their used temps, in one walk of
// the instructions beside the function's table of used temps.
let recursive regionInstructionsAlong (instructions: List(IrInstruction)) (uses: List(List(Int))) (index: Int) (spans: List((Int, Int, Int))) =
    match spans with
        | [] -> []
        | (_block, start, end) :: restSpans ->
            if index >= end
            then regionInstructionsAlong(instructions)(uses)(index)(restSpans)
            else
                match (instructions, uses) with
                    | (head :: rest, used :: restUses) ->
                        if index >= start
                        then (index, head, used) :: regionInstructionsAlong(rest)(restUses)(index + 1)(spans)
                        else regionInstructionsAlong(rest)(restUses)(index + 1)(spans)
                    | _ -> []

let recursive anyTempInSet (temps: List(Int)) (set: MapTree(Int, Bool)) =
    match temps with
        | [] -> false
        | temp :: rest -> inTempSet(temp)(set) || anyTempInSet(rest)(set)

// Each region block's owner loads and uses, in one walk of the region's instructions along the
// spans: the indices gather newest first and are turned when the walk leaves the block.
let recursive blockFactsAlong (indexed: List((Int, IrInstruction, List(Int)))) (spans: List((Int, Int, Int))) (slot: Int) (aliases: MapTree(Int, Bool)) (loads: List(Int)) (uses: List(Int)) =
    match spans with
        | [] -> []
        | (blockIndex, _start, end) :: restSpans ->
            match indexed with
                | [] -> BlockFacts(factsBlock = blockIndex, factsLoads = reverse(loads), factsUses = reverse(uses)) :: blockFactsAlong([])(restSpans)(slot)(aliases)([])([])
                | (index, IrInstruction { instruction = kind }, used) :: rest ->
                    if index >= end
                    then BlockFacts(factsBlock = blockIndex, factsLoads = reverse(loads), factsUses = reverse(uses)) :: blockFactsAlong(indexed)(restSpans)(slot)(aliases)([])([])
                    else
                        match kind with
                            | LoadLocal(_target, candidate) ->
                                if candidate == slot
                                then blockFactsAlong(rest)(spans)(slot)(aliases)(index :: loads)(index :: uses)
                                else blockFactsAlong(rest)(spans)(slot)(aliases)(loads)(uses)
                            | _ ->
                                if anyTempInSet(used)(aliases)
                                then blockFactsAlong(rest)(spans)(slot)(aliases)(loads)(index :: uses)
                                else blockFactsAlong(rest)(spans)(slot)(aliases)(loads)(uses)

// The used temps of every instruction, in the instructions' order: worked out once per function
// and kept beside the instructions through every owner's edits.
let recursive usedTempsOf (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | IrInstruction { instruction = kind } :: rest -> getUsedTemps(kind) :: usedTempsOf(rest)

let recursive usedTempsInsertedAt (index: Int) (insertions: List((Int, IrInstruction))) =
    match insertions with
        | [] -> []
        | (candidate, IrInstruction { instruction = kind }) :: rest ->
            if candidate == index
            then getUsedTemps(kind) :: usedTempsInsertedAt(index)(rest)
            else usedTempsInsertedAt(index)(rest)

// The table of used temps after the insertions `applyInsertions` makes at the same indices.
let recursive applyUseInsertions (indices: List(Int)) (insertions: List((Int, IrInstruction))) (uses: List(List(Int))) =
    match indices with
        | [] -> uses
        | index :: rest ->
            uses
            |> insertAllAt(index)(usedTempsInsertedAt(index)(insertions))
            |> applyUseInsertions(rest)(insertions)

// The sources of the copy-outs that release an arena cell's children (a tail-call back edge's
// copy of a by-name arena successor); placement neither adds nor removes one.
let recursive normalizingCopySources (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | IrInstruction { instruction = CopyOutArena(_dest, src, _size, true, RcNormalization, _semanticType) } :: rest -> src :: normalizingCopySources(rest)
        | _ :: rest -> normalizingCopySources(rest)

// What one walk of the instructions finds for an owner slot: how many anchors name it, the first
// anchor with its index and whether the owner load right before it feeds it, the slot's first
// store, and how many instructions there are.
type AnchorScan =
    | scanAnchorCount: Int
    | scanAnchorIndex: Int
    | scanLoadFeedsAnchor: Bool
    | scanAnchor: Maybe(OwnerAnchor)
    | scanDefinitionIndex: Int
    | scanDefinitionTemp: Int
    | scanInstructionCount: Int

let recursive anchorScanFrom (instructions: List(IrInstruction)) (slot: Int) (index: Int) (afterOwnerLoad: Bool) (loadTarget: Int) (anchorCount: Int) (anchorIndex: Int) (loadFeeds: Bool) (anchor: Maybe(OwnerAnchor)) (definitionIndex: Int) (definitionTemp: Int) =
    match instructions with
        | [] -> AnchorScan(scanAnchorCount = anchorCount, scanAnchorIndex = anchorIndex, scanLoadFeedsAnchor = loadFeeds, scanAnchor = anchor, scanDefinitionIndex = definitionIndex, scanDefinitionTemp = definitionTemp, scanInstructionCount = index)
        | IrInstruction { instruction = kind, location = location } :: rest ->
            match kind with
                | RcDrop(source, typeName, ownerSlot, runtimeManaged, mayBeEmpty, dropper) ->
                    if ownerSlot == slot
                    then
                        if anchorCount == 0
                        then anchorScanFrom(rest)(slot)(index + 1)(false)(0)(1)(index)(afterOwnerLoad && loadTarget == source)(Some(OwnerAnchor(anchorTypeName = typeName, anchorRuntimeManaged = runtimeManaged, anchorMayBeEmpty = mayBeEmpty, anchorStructuralDropper = dropper, anchorLocation = location)))(definitionIndex)(definitionTemp)
                        else anchorScanFrom(rest)(slot)(index + 1)(false)(0)(anchorCount + 1)(anchorIndex)(loadFeeds)(anchor)(definitionIndex)(definitionTemp)
                    else anchorScanFrom(rest)(slot)(index + 1)(false)(0)(anchorCount)(anchorIndex)(loadFeeds)(anchor)(definitionIndex)(definitionTemp)
                | LoadLocal(target, loadSlot) -> anchorScanFrom(rest)(slot)(index + 1)(loadSlot == slot)(target)(anchorCount)(anchorIndex)(loadFeeds)(anchor)(definitionIndex)(definitionTemp)
                | StoreLocal(candidate, source) ->
                    if candidate == slot && definitionIndex < 0
                    then anchorScanFrom(rest)(slot)(index + 1)(false)(0)(anchorCount)(anchorIndex)(loadFeeds)(anchor)(index)(source)
                    else anchorScanFrom(rest)(slot)(index + 1)(false)(0)(anchorCount)(anchorIndex)(loadFeeds)(anchor)(definitionIndex)(definitionTemp)
                | _ -> anchorScanFrom(rest)(slot)(index + 1)(false)(0)(anchorCount)(anchorIndex)(loadFeeds)(anchor)(definitionIndex)(definitionTemp)

let anchorScanOf (instructions: List(IrInstruction)) (slot: Int) = anchorScanFrom(instructions)(slot)(0)(false)(0)(0)(-1)(false)(None)(-1)(0)

// Takes `count` instructions out at `first`, in one walk.
let recursive removeSpan items first count =
    match items with
        | [] -> []
        | head :: rest ->
            if first == 0
            then
                if count <= 1
                then rest
                else removeSpan(rest)(0)(count - 1)
            else head :: removeSpan(rest)(first - 1)(count)

let recursive arenaAdtCellSetInto (instructions: List(IrInstruction)) (cells: MapTree(Int, Bool)) =
    match instructions with
        | [] -> cells
        | head :: rest ->
            let target = arenaAdtCellTarget(head)
            in
                if target >= 0
                then
                    cells
                    |> Ashes.Collection.Map.setInt(target)(true)
                    |> arenaAdtCellSetInto(rest)
                else arenaAdtCellSetInto(rest)(cells)

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
// A branch whose condition is an alias (a call's result, read only as a truth value or a tag) ends
// its block: the drop goes before it, so every path out of the block takes it.
let lifetimeInsertionIndex (instructions: List(IrInstruction)) (lastUse: Int) =
    match (listAt(instructions)(lastUse), listAt(instructions)(lastUse + 1)) with
        | (Some(IrInstruction { instruction = Return(_result) }), _) -> lastUse
        | (Some(IrInstruction { instruction = JumpIfFalse(_condition, _target) }), _) -> lastUse
        | (Some(IrInstruction { instruction = SwitchTag(_tag, _cases, _defaultLabel) }), _) -> lastUse
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

// Whether taking `removed` instructions out at `first` leaves every block in place: they sit in
// one block that keeps an instruction of its own. They are an anchor and its load, neither a label
// nor a terminator, so that block still opens and ends as it did.
let recursive removalKeepsBlocks (blocks: List(IrCfgBlock)) (first: Int) (removed: Int) =
    match blocks with
        | [] -> false
        | IrCfgBlock { blockStart = start, blockEnd = end } :: rest ->
            if first >= end
            then removalKeepsBlocks(rest)(first)(removed)
            else first >= start && first + removed <= end && end - start > removed

// The blocks once `removed` instructions are gone from the block holding `first`.
let recursive blocksWithout (blocks: List(IrCfgBlock)) (first: Int) (removed: Int) (shift: Int) =
    match blocks with
        | [] -> []
        | block :: rest ->
            if shift > 0
            then (block with blockStart = block.blockStart - shift, blockEnd = block.blockEnd - shift) :: blocksWithout(rest)(first)(removed)(shift)
            else
                if first >= block.blockEnd
                then block :: blocksWithout(rest)(first)(removed)(0)
                else (block with blockEnd = block.blockEnd - removed) :: blocksWithout(rest)(first)(removed)(removed)

// The carried blocks after an anchor's removal, none when a block would be emptied.
let carriedBlocksWithout (blocks: List(IrCfgBlock)) (first: Int) (removed: Int) =
    if removalKeepsBlocks(blocks)(first)(removed)
    then blocksWithout(blocks)(first)(removed)(0)
    else []

let recursive insertionsAt (index: Int) (insertions: List((Int, IrInstruction))) (count: Int) =
    match insertions with
        | [] -> count
        | (candidate, _instruction) :: rest ->
            if candidate == index
            then insertionsAt(index)(rest)(count + 1)
            else insertionsAt(index)(rest)(count)

let recursive insertionCounts (indices: List(Int)) (insertions: List((Int, IrInstruction))) =
    match indices with
        | [] -> []
        | index :: rest -> (index, insertionsAt(index)(insertions)(0)) :: insertionCounts(rest)(insertions)

let nextOpensWithLabel (kinds: List((Bool, Bool))) =
    match kinds with
        | (opensWithLabel, _endsInTerminator) :: _ -> opensWithLabel
        | [] -> false

// How many of the inserted instructions each block takes, the insertions ascending by index;
// `[-1]` when one of them would open a block of its own. Drops and dups are neither labels nor
// terminators, so one inside a block grows it; one at the start of a block that opens after a
// terminator leads that block; one before a block's opening label trails the block falling
// through into it. One right after a terminator and before a label, or past the last
// instruction, is a block no span covers.
let recursive insertedByBlock (blocks: List(IrCfgBlock)) (kinds: List((Bool, Bool))) (inserts: List((Int, Int))) (own: Int) (counts: List(Int)) =
    match (blocks, kinds) with
        | (IrCfgBlock { blockStart = start, blockEnd = end } :: restBlocks, (opensWithLabel, endsInTerminator) :: restKinds) ->
            match inserts with
                | [] -> insertedByBlock(restBlocks)(restKinds)([])(0)(own :: counts)
                | (index, added) :: restInserts ->
                    if index < start || index == start && opensWithLabel
                    then [-1]
                    else
                        if index < end || index == end && endsInTerminator == false && nextOpensWithLabel(restKinds)
                        then insertedByBlock(blocks)(kinds)(restInserts)(own + added)(counts)
                        else insertedByBlock(restBlocks)(restKinds)(inserts)(0)(own :: counts)
        | _ ->
            match inserts with
                | [] -> reverse(counts)
                | _ -> [-1]

let recursive blocksWithInserted (blocks: List(IrCfgBlock)) (counts: List(Int)) (shift: Int) =
    match (blocks, counts) with
        | (block :: restBlocks, own :: restCounts) -> (block with blockStart = block.blockStart + shift, blockEnd = block.blockEnd + shift + own) :: blocksWithInserted(restBlocks)(restCounts)(shift + own)
        | _ -> []

// The blocks of the instructions an owner's edits produce, out of the blocks they were placed
// against: the same blocks with their spans moved while every edit stays inside a block, and none
// (the next owner builds them again) once a jump was retargeted or an insertion opens a block.
let carriedBlocksAfter (blocks: List(IrCfgBlock)) (kinds: List((Bool, Bool))) (indicesDescending: List(Int)) (insertions: List((Int, IrInstruction))) (retargets: List((Int, IrInstruction))) =
    match retargets with
        | [] ->
            match insertions with
                | [] -> blocks
                | _ ->
                    match insertedByBlock(blocks)(kinds)(insertionCounts(reverse(indicesDescending))(insertions))(0)([]) with
                        | -1 :: _ -> []
                        | counts -> blocksWithInserted(blocks)(counts)(0)
        | _ -> []

let recursive regionBlocksFrom (blocks: List(IrCfgBlock)) (index: Int) (region: List(Int)) =
    match region with
        | [] -> []
        | blockIndex :: restRegion ->
            match blocks with
                | [] -> []
                | block :: restBlocks ->
                    if index == blockIndex
                    then block :: regionBlocksFrom(restBlocks)(index + 1)(restRegion)
                    else regionBlocksFrom(restBlocks)(index + 1)(region)

let recursive blockSetOf (blocks: List(Int)) (set: MapTree(Int, Bool)) =
    match blocks with
        | [] -> set
        | block :: rest ->
            set
            |> Ashes.Collection.Map.setInt(block)(true)
            |> blockSetOf(rest)

// The blocks reachable from `start` that it dominates and that lie before the boundary (an earlier
// owner's split edge rejoining the region included), as a set.
let recursive reachableSetBeforeBoundary (instructions: List(IrInstruction)) (label: Str) (blocks: List(IrCfgBlock)) (successors: MapTree(Int, List(Int))) (dominance: DominanceIndex) (start: Int) (boundary: Int) (pending: List(Int)) (reachable: MapTree(Int, Bool)) =
    match pending with
        | [] -> reachable
        | current :: rest ->
            if current > boundary && isSplitEdgeWithin(instructions)(blocks)(label)(boundary)(current) == false || dominatesWithin(dominance)(start)(current) == false || inBlockSet(current)(reachable)
            then reachableSetBeforeBoundary(instructions)(label)(blocks)(successors)(dominance)(start)(boundary)(rest)(reachable)
            else
                if current == boundary
                then
                    reachable
                    |> Ashes.Collection.Map.setInt(current)(true)
                    |> reachableSetBeforeBoundary(instructions)(label)(blocks)(successors)(dominance)(start)(boundary)(rest)
                else
                    reachable
                    |> Ashes.Collection.Map.setInt(current)(true)
                    |> reachableSetBeforeBoundary(instructions)(label)(blocks)(successors)(dominance)(start)(boundary)(append(edgesAt(current)(successors))(rest))

// The members of `visited` among the block indices up to `index`, ascending.
let recursive sortedBlocksIn (visited: MapTree(Int, Bool)) (index: Int) (acc: List(Int)) =
    if index < 0
    then acc
    else
        if inBlockSet(index)(visited)
        then sortedBlocksIn(visited)(index - 1)(index :: acc)
        else sortedBlocksIn(visited)(index - 1)(acc)

// The blocks with a use of the owner: live on entry, and where the liveness walk starts.
let recursive usedBlocksInto (facts: List(BlockFacts)) (liveIn: MapTree(Int, Bool)) (worklist: List(Int)) =
    match facts with
        | [] -> (liveIn, worklist)
        | BlockFacts { factsBlock = blockIndex, factsUses = uses } :: rest ->
            match uses with
                | [] -> usedBlocksInto(rest)(liveIn)(worklist)
                | _ ->
                    if inBlockSet(blockIndex)(liveIn)
                    then usedBlocksInto(rest)(liveIn)(worklist)
                    else
                        usedBlocksInto(rest)(Ashes.Collection.Map.setInt(blockIndex)(true)(liveIn))(blockIndex :: worklist)

// The owner's liveness over the region as the least solution of: live out of a block when live
// into a successor in the region other than the definition block, live into a block when it has a
// use or is live out of it. A block newly live on entry makes its region predecessors live on
// exit, and through that on entry, until nothing is left to pass on. A back edge into the block
// that defines the owner re-runs the definition, which binds the next iteration's own reference:
// the owner reaching that edge is dead there, not live around the loop.
let recursive liveBlocksFrom (definitionBlock: Int) (region: MapTree(Int, Bool)) (predecessors: MapTree(Int, List(Int))) (sources: List(Int)) (worklist: List(Int)) (liveIn: MapTree(Int, Bool)) (liveOut: MapTree(Int, Bool)) =
    match sources with
        | source :: rest ->
            if inBlockSet(source)(region) && inBlockSet(source)(liveOut) == false
            then
                if inBlockSet(source)(liveIn)
                then
                    liveOut
                    |> Ashes.Collection.Map.setInt(source)(true)
                    |> liveBlocksFrom(definitionBlock)(region)(predecessors)(rest)(worklist)(liveIn)
                else
                    liveOut
                    |> Ashes.Collection.Map.setInt(source)(true)
                    |> liveBlocksFrom(definitionBlock)(region)(predecessors)(rest)(source :: worklist)(Ashes.Collection.Map.setInt(source)(true)(liveIn))
            else liveBlocksFrom(definitionBlock)(region)(predecessors)(rest)(worklist)(liveIn)(liveOut)
        | [] ->
            match worklist with
                | [] -> (liveIn, liveOut)
                | block :: more ->
                    if block == definitionBlock
                    then liveBlocksFrom(definitionBlock)(region)(predecessors)([])(more)(liveIn)(liveOut)
                    else
                        liveBlocksFrom(definitionBlock)(region)(predecessors)(edgesAt(block)(predecessors))(more)(liveIn)(liveOut)

let recursive livenessList (region: List(Int)) (liveIn: MapTree(Int, Bool)) (liveOut: MapTree(Int, Bool)) =
    match region with
        | [] -> []
        | blockIndex :: rest -> BlockLiveness(livenessBlock = blockIndex, livenessIn = inBlockSet(blockIndex)(liveIn), livenessOut = inBlockSet(blockIndex)(liveOut)) :: livenessList(rest)(liveIn)(liveOut)

let isLiveBranchOut (predecessor: Int) (successors: MapTree(Int, List(Int))) (liveOut: MapTree(Int, Bool)) =
    inBlockSet(predecessor)(liveOut) && length(edgesAt(predecessor)(successors)) > 1

let recursive anyLiveBranchOut (predecessors: List(Int)) (successors: MapTree(Int, List(Int))) (liveOut: MapTree(Int, Bool)) =
    match predecessors with
        | [] -> false
        | predecessor :: rest -> isLiveBranchOut(predecessor)(successors)(liveOut) || anyLiveBranchOut(rest)(successors)(liveOut)

let recursive allLiveOut (predecessors: List(Int)) (liveOut: MapTree(Int, Bool)) =
    match predecessors with
        | [] -> true
        | predecessor :: rest -> inBlockSet(predecessor)(liveOut) && allLiveOut(rest)(liveOut)

let recursive splitLiveEdgesOut (instructions: List(IrInstruction)) (blocks: List(IrCfgBlock)) (successors: MapTree(Int, List(Int))) (liveOut: MapTree(Int, Bool)) (predecessors: List(Int)) (joinIndex: Int) (drop: IrInstruction) (label: Str) (slot: Int) =
    match predecessors with
        | [] -> ([], [])
        | predecessor :: rest ->
            match (splitLiveEdgesOut(instructions)(blocks)(successors)(liveOut)(rest)(joinIndex)(drop)(label)(slot), isLiveBranchOut(predecessor)(successors)(liveOut)) with
                | ((restInsertions, restRetargets), true) ->
                    match splitLiveEdge(instructions)(blocks)(predecessor)(joinIndex)(drop)(label)(slot) with
                        | (edgeInsertions, edgeRetargets) -> (append(edgeInsertions)(restInsertions), append(edgeRetargets)(restRetargets))
                | (restSplit, false) -> restSplit

let recursive dupsAfterLoad (indexed: List((Int, IrInstruction))) (aliases: List(Int)) (keepsLiveAfter: Bool) (anchor: OwnerAnchor) (tempCount: Int) (insertions: List((Int, IrInstruction))) (borrowingArenaCells: MapTree(Int, Bool)) (borrowedCalls: List(Int)) =
    match indexed with
        | [] -> (insertions, tempCount)
        | (index, IrInstruction { instruction = kind, location = location }) :: rest ->
            match kind with
                | Borrow(target, source) ->
                    if sortedSetContains(source)(aliases)
                    then
                        dupsAfterLoad(rest)(sortedSetInsert(target)(aliases))(keepsLiveAfter)(anchor)(tempCount)(insertions)(borrowingArenaCells)(borrowedCalls)
                    else dupsAfterLoad(rest)(aliases)(keepsLiveAfter)(anchor)(tempCount)(insertions)(borrowingArenaCells)(borrowedCalls)
                | SetAdtField(ptr, _fieldIndex, source, _tagless) ->
                    if sortedSetContains(source)(aliases) && inBlockSet(ptr)(borrowingArenaCells) == false
                    then
                        dupsAfterLoad(rest)(aliases)(keepsLiveAfter)(anchor)(tempCount + 1)(append(insertions)([(index, IrInstruction(instruction = RcDup(tempCount)(source)(anchor.anchorRuntimeManaged)(anchor.anchorMayBeEmpty), location = location))]))(borrowingArenaCells)(borrowedCalls)
                    else dupsAfterLoad(rest)(aliases)(keepsLiveAfter)(anchor)(tempCount)(insertions)(borrowingArenaCells)(borrowedCalls)
                | CallClosure(target, _closure, argument, _flag) ->
                    if sortedSetContains(argument)(aliases) && keepsLiveAfter && sortedSetContains(target)(borrowedCalls) == false
                    then (append(insertions)([(index, IrInstruction(instruction = RcDup(tempCount)(argument)(anchor.anchorRuntimeManaged)(anchor.anchorMayBeEmpty), location = location))]), tempCount + 1)
                    else dupsAfterLoad(rest)(aliases)(keepsLiveAfter)(anchor)(tempCount)(insertions)(borrowingArenaCells)(borrowedCalls)
                | _ -> dupsAfterLoad(rest)(aliases)(keepsLiveAfter)(anchor)(tempCount)(insertions)(borrowingArenaCells)(borrowedCalls)

let recursive dupsAfterLoads (instructions: List(IrInstruction)) (blockEnd: Int) (loads: List(Int)) (liveOut: Bool) (anchor: OwnerAnchor) (tempCount: Int) (insertions: List((Int, IrInstruction))) (borrowingArenaCells: MapTree(Int, Bool)) (borrowedCalls: List(Int)) =
    match loads with
        | [] -> (insertions, tempCount)
        | loadIndex :: rest ->
            match listAt(instructions)(loadIndex) with
                | Some(IrInstruction { instruction = LoadLocal(sourceTemp, _slot) }) ->
                    match dupsAfterLoad(indexedRange(instructions)(0)(loadIndex + 1)(blockEnd))([sourceTemp])(length(rest) > 0 || liveOut)(anchor)(tempCount)(insertions)(borrowingArenaCells)(borrowedCalls) with
                        | (nextInsertions, nextTempCount) -> dupsAfterLoads(instructions)(blockEnd)(rest)(liveOut)(anchor)(nextTempCount)(nextInsertions)(borrowingArenaCells)(borrowedCalls)
                | _ -> dupsAfterLoads(instructions)(blockEnd)(rest)(liveOut)(anchor)(tempCount)(insertions)(borrowingArenaCells)(borrowedCalls)

// The region's blocks, their facts and their liveness run side by side, one entry per block.
let recursive collectInsertionsAlong (instructions: List(IrInstruction)) (blocks: List(IrCfgBlock)) (successors: MapTree(Int, List(Int))) (liveOutSet: MapTree(Int, Bool)) (definitionBlock: Int) (owner: OwnerRegion) (slot: Int) (anchor: OwnerAnchor) (label: Str) (regionBlocks: List(IrCfgBlock)) (facts: List(BlockFacts)) (liveness: List(BlockLiveness)) (tempCount: Int) (insertions: List((Int, IrInstruction))) (retargets: List((Int, IrInstruction))) (borrowingArenaCells: MapTree(Int, Bool)) (borrowedCalls: List(Int)) =
    match (regionBlocks, facts, liveness) with
        | (IrCfgBlock { blockStart = start, blockEnd = end, blockPredecessors = predecessors } :: restBlocks, BlockFacts { factsBlock = blockIndex, factsLoads = loads, factsUses = uses } :: restFacts, BlockLiveness { livenessIn = liveIn, livenessOut = liveOut } :: restLiveness) ->
            ((given (dropsAndRetargets) ->
                match dropsAndRetargets with
                    | (drops, edgeRetargets) ->
                        match dupsAfterLoads(instructions)(end)(loads)(liveOut)(anchor)(tempCount)(append(insertions)(drops))(borrowingArenaCells)(borrowedCalls) with
                            | (nextInsertions, nextTempCount) ->
                                collectInsertionsAlong(instructions)(blocks)(successors)(liveOutSet)(definitionBlock)(owner)(slot)(anchor)(label)(restBlocks)(restFacts)(restLiveness)(nextTempCount)(nextInsertions)(append(retargets)(edgeRetargets))(borrowingArenaCells)(borrowedCalls)))(if length(uses) > 0 && liveOut == false
            then
                ([(uses
                |> lastOf
                |> lifetimeInsertionIndex(instructions), placedDrop(anchor)(slot)(owner))], [])
            else
                if liveIn == false && blockIndex == definitionBlock
                then ([(owner.regionDefinitionIndex + 1, placedDrop(anchor)(slot)(owner))], [])
                else
                    if liveIn == false && anyLiveBranchOut(predecessors)(successors)(liveOutSet)
                    then
                        if allLiveOut(predecessors)(liveOutSet)
                        then
                            match listAt(instructions)(start) with
                                | Some(IrInstruction { instruction = Label(_name) }) -> ([(start + 1, placedDrop(anchor)(slot)(owner))], [])
                                | _ -> ([(start, placedDrop(anchor)(slot)(owner))], [])
                        else
                            splitLiveEdgesOut(instructions)(blocks)(successors)(liveOutSet)(predecessors)(blockIndex)(placedDrop(anchor)(slot)(owner))(label)(slot)
                    else ([], []))
        | _ -> (insertions, retargets, tempCount)

let placedGraphBlocks (placed: PlacedInstructions) =
    (let graph = placed.placedGraph
    in graph.graphBlocks)

// The function's table of used temps for the instructions being placed, their count, and the
// sources of its normalizing copy-outs.
type OwnerTables =
    | tableUses: List(List(Int))
    | tableCount: Int
    | tableCopySources: List(Int)

let placeOwnerInRegion (instructions: List(IrInstruction)) (tables: OwnerTables) (slot: Int) (owner: OwnerRegion) (anchor: OwnerAnchor) (label: Str) (tempCount: Int) (graph: OwnerGraph) (definitionBlock: Int) (region: List(Int)) (arenaAdtCells: MapTree(Int, Bool)) (borrowedCalls: List(Int)) =
    (let spans = regionSpansFrom(graph.graphBlocks)(0)(region)
    in
        let indexed = regionInstructionsAlong(instructions)(tables.tableUses)(0)(spans)
        in
            let ownerAliases = collectOwnerAliasSet(spans)(indexed)(slot)(arenaAdtCells)
            in
                let facts = blockFactsAlong(indexed)(spans)(slot)(ownerAliases)([])([])
                in
                    match match usedBlocksInto(facts)(Ashes.Collection.Map.empty)([]) with
                        | (usedBlocks, worklist) ->
                            liveBlocksFrom(definitionBlock)(blockSetOf(region)(Ashes.Collection.Map.empty))(graphPredecessors(graph))([])(worklist)(usedBlocks)(Ashes.Collection.Map.empty) with
                        | (liveIn, liveOut) ->
                            let liveness = livenessList(region)(liveIn)(liveOut)
                            in
                                let cells =
                                    if ownerAliases |> anyTempInSet(tables.tableCopySources)
                                    then Ashes.Collection.Map.empty
                                    else arenaAdtCells
                                in
                                    match borrowedCalls |> collectInsertionsAlong(instructions)(graph.graphBlocks)(graphSuccessors(graph))(liveOut)(definitionBlock)(owner)(slot)(anchor)(label)(regionBlocksFrom(graph.graphBlocks)(0)(region))(facts)(liveness)(tempCount)([])([])(cells) with
                                        | (insertions, retargets, nextTempCount) ->
                                            let indices = distinctIndicesDescending(insertions)([])
                                            in
                                                PlacedInstructions(placedInstructions = instructions
                                                |> applyRetargets(retargets)
                                                |> applyInsertions(indices)(insertions), placedTempCount = nextTempCount, placedGraph = (graph with graphBlocks = carriedBlocksAfter(graph.graphBlocks)(graph.graphKinds)(indices)(insertions)(retargets)), placedUses = applyUseInsertions(indices)(insertions)(tables.tableUses), placedCopySources = tables.tableCopySources))

let unchangedPlacement (instructions: List(IrInstruction)) (tables: OwnerTables) (tempCount: Int) (graph: OwnerGraph) = PlacedInstructions(placedInstructions = instructions, placedTempCount = tempCount, placedGraph = graph, placedUses = tables.tableUses, placedCopySources = tables.tableCopySources)

let placeOwnerBetween (instructions: List(IrInstruction)) (tables: OwnerTables) (slot: Int) (owner: OwnerRegion) (anchor: OwnerAnchor) (label: Str) (tempCount: Int) (graph: OwnerGraph) (definitionBlock: Int) (boundaryBlock: Int) (arenaAdtCells: MapTree(Int, Bool)) (borrowedCalls: List(Int)) =
    (let region =
        sortedBlocksIn(reachableSetBeforeBoundary(instructions)(label)(graph.graphBlocks)(graphSuccessors(graph))(graph.graphDominance)(definitionBlock)(boundaryBlock)([definitionBlock])(Ashes.Collection.Map.empty))(length(graph.graphBlocks) - 1)([])
    in
        if length(region) == 0
        then unchangedPlacement(instructions)(tables)(tempCount)(graph)
        else placeOwnerInRegion(instructions)(tables)(slot)(owner)(anchor)(label)(tempCount)(graph)(definitionBlock)(region)(arenaAdtCells)(borrowedCalls))

// The dominators are the ones carried from an earlier owner while the block count is unchanged, as
// stage 0's PlaceOwner keeps them, and worked out again with their index otherwise.
let withCurrentDominators (graph: OwnerGraph) =
    if length(graph.graphDominators) == length(graph.graphBlocks)
    then graph
    else
        let dominators = graph.graphEdges |> immediateDominatorsOf(graph.graphBlocks)
        in graph with graphDominators = dominators, graphDominance = dominanceIndexOf(dominators)

let placeOwnerAgainst (instructions: List(IrInstruction)) (tables: OwnerTables) (slot: Int) (owner: OwnerRegion) (anchor: OwnerAnchor) (label: Str) (tempCount: Int) (graph: OwnerGraph) (arenaAdtCells: MapTree(Int, Bool)) (borrowedCalls: List(Int)) =
    match (findCfgBlock(graph.graphBlocks)(owner.regionDefinitionIndex), owner
    |> boundaryIndexWithin(tables.tableCount)
    |> findCfgBlock(graph.graphBlocks)) with
        | (definitionBlock, boundaryBlock) ->
            if definitionBlock < 0 || boundaryBlock < 0
            then unchangedPlacement(instructions)(tables)(tempCount)(graph)
            else
                placeOwnerBetween(instructions)(tables)(slot)(owner)(anchor)(label)(tempCount)(withCurrentDominators(graph))(definitionBlock)(boundaryBlock)(arenaAdtCells)(borrowedCalls)

// The blocks are the ones carried from the owner before when its edits kept every block in place
// (`carried`, with their kinds and edges), and built from the instructions otherwise.
let placeOwnerRegion (instructions: List(IrInstruction)) (tables: OwnerTables) (slot: Int) (owner: OwnerRegion) (anchor: OwnerAnchor) (label: Str) (tempCount: Int) (known: OwnerGraph) (carried: List(IrCfgBlock)) (arenaAdtCells: MapTree(Int, Bool)) (borrowedCalls: List(Int)) =
    match carried with
        | [] ->
            let blocks = buildCfgBlocks(instructions)
            in placeOwnerAgainst(instructions)(tables)(slot)(owner)(anchor)(label)(tempCount)((known with graphBlocks = blocks, graphKinds = blockBoundaryKinds(instructions)(blocks), graphEdges = blockEdgesOf(blocks)))(arenaAdtCells)(borrowedCalls)
        | _ -> placeOwnerAgainst(instructions)(tables)(slot)(owner)(anchor)(label)(tempCount)((known with graphBlocks = carried))(arenaAdtCells)(borrowedCalls)

// Removes the lexical anchor (and the owner load feeding it) in one walk of the instructions and
// of their used temps, and places the owner's drop.
let placeOwner (label: Str) (slot: Int) (scan: AnchorScan) (arenaAdtCells: MapTree(Int, Bool)) (borrowedCalls: List(Int)) (placed: PlacedInstructions) =
    match (scan.scanAnchor, scan.scanDefinitionIndex >= 0) with
        | (Some(anchor), true) ->
            let removed =
                if scan.scanLoadFeedsAnchor
                then 2
                else 1
            in
                let boundary = scan.scanAnchorIndex + 1 - removed
                in
                    placeOwnerRegion(removeSpan(placed.placedInstructions)(boundary)(removed))(OwnerTables(tableUses = removeSpan(placed.placedUses)(boundary)(removed), tableCount = scan.scanInstructionCount - removed, tableCopySources = placed.placedCopySources))(slot)(OwnerRegion(regionDefinitionIndex = scan.scanDefinitionIndex, regionBoundaryIndex = boundary, regionDefinitionTemp = scan.scanDefinitionTemp))(anchor)(label)(placed.placedTempCount)(placed.placedGraph)(carriedBlocksWithout(placedGraphBlocks(placed))(boundary)(removed))(arenaAdtCells)(borrowedCalls)
        | _ -> placed

let recursive placeOwnerSlots (label: Str) (slots: List(Int)) (arenaAdtCells: MapTree(Int, Bool)) (borrowedCalls: List(Int)) (placed: PlacedInstructions) =
    match slots with
        | [] -> placed
        | slot :: rest ->
            let scan = slot |> anchorScanOf(placed.placedInstructions)
            in
                placeOwnerSlots(label)(rest)(arenaAdtCells)(borrowedCalls)(if scan.scanAnchorCount == 1
                then placeOwner(label)(slot)(scan)(arenaAdtCells)(borrowedCalls)(placed)
                else placed)

let unplacedGraphOf (blockCount: Int) = OwnerGraph(graphBlocks = [], graphKinds = [], graphEdges = BlockEdges(successorsOfBlock = Ashes.Collection.Map.empty, predecessorsOfBlock = Ashes.Collection.Map.empty), graphDominators = [], graphDominance = DominanceIndex(dominanceEnter = Ashes.Collection.Map.empty, dominanceLeave = Ashes.Collection.Map.empty))

// Places the lifetimes of one function body; `label` names the function, prefixing the labels of
// the drop blocks that split a live branch edge. `borrowedCalls` are the result temps of the
// body's closure calls that only borrow their argument, which get no compensating duplicate. A
// body with no anchored owner slot is left as it is, without its tables being built.
let placeInstructionLifetimesBorrowing (label: Str) (borrowedCalls: List(Int)) (instructions: List(IrInstruction)) (tempCount: Int) =
    match ownerSlots(instructions)([]) with
        | [] -> PlacedInstructions(placedInstructions = instructions, placedTempCount = tempCount, placedGraph = unplacedGraphOf(0), placedUses = [], placedCopySources = [])
        | slots ->
            placeOwnerSlots(label)(slots)(arenaAdtCellSetInto(instructions)(Ashes.Collection.Map.empty))(borrowedCalls)(PlacedInstructions(placedInstructions = instructions, placedTempCount = tempCount, placedGraph = unplacedGraphOf(0), placedUses = usedTempsOf(instructions), placedCopySources = normalizingCopySources(instructions)))

let placeInstructionLifetimesIn (label: Str) (instructions: List(IrInstruction)) (tempCount: Int) = placeInstructionLifetimesBorrowing(label)([])(instructions)(tempCount)

let placeInstructionLifetimes (instructions: List(IrInstruction)) (tempCount: Int) = placeInstructionLifetimesIn("")(instructions)(tempCount)

// The borrowing calls grouped by the label of the function holding them.
let recursive borrowedCallsByLabel (borrowedCalls: List((Str, Int))) byLabel =
    match borrowedCalls with
        | [] -> byLabel
        | (label, target) :: rest ->
            byLabel
            |> Ashes.Collection.Map.upsertStr(label)([target])(given (targets: List(Int)) -> sortedSetInsert(target)(targets))
            |> borrowedCallsByLabel(rest)

let placeFunctionLifetimesBorrowing byLabel (function_: IrFunction) =
    if function_.lifetimesPlaced
    then function_
    else
        let borrowedCalls =
            match Ashes.Collection.Map.getStr(function_.label)(byLabel) with
                | Some(targets) -> targets
                | None -> []
        in
            match placeInstructionLifetimesBorrowing(function_.label)(borrowedCalls)(function_.instructions)(function_.tempCount) with
                | PlacedInstructions { placedInstructions = instructions, placedTempCount = tempCount } -> function_ with instructions = instructions, tempCount = tempCount, lifetimesPlaced = true

let placeFunctionLifetimes (function_: IrFunction) = placeFunctionLifetimesBorrowing(Ashes.Collection.Map.empty)(function_)

// `borrowedCalls` names each borrowing closure call by its function's label and its result temp.
let placeLifetimes (borrowedCalls: List((Str, Int))) (program: IrProgram) =
    (let byLabel = borrowedCallsByLabel(borrowedCalls)(Ashes.Collection.Map.empty)
    in
        program with entryFunction = placeFunctionLifetimesBorrowing(byLabel)(program.entryFunction), functions = map(placeFunctionLifetimesBorrowing(byLabel))(program.functions))
