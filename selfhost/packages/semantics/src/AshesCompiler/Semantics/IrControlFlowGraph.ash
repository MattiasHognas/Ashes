// Basic blocks and dominators over one function's instruction list, stage 0's
// `IrControlFlowGraph`. A block starts at index 0, at every `Label`, and right after every
// terminator (`Jump`, `JumpIfFalse`, `SwitchTag`, `Return`); its edges follow its last
// instruction: a jump to its label, a conditional jump to its label and then the fall-through
// block, a switch to every case label and then the default, a return nowhere, and anything else
// the fall-through. Successor and predecessor lists keep edge discovery order with one entry per
// edge. A set of block indices is a sorted list without duplicates. Dominance is kept as the
// immediate dominator of every block, indexed by block and refined over the reverse postorder of
// the blocks reachable from block 0 until it settles (an unreachable block has none and is
// dominated only by itself), and answered by walking that tree.
import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.List.sortBy
import Ashes.Collection.Map
import Ashes.Collection.Map.MapTree
import AshesCompiler.Semantics.IrInstructions
export (
    type IrCfgBlock(..),
    value listAt,
    value containsInt,
    value sortedSetContains,
    value sortedSetInsert,
    value buildCfgBlocks,
    value blockBoundaryKinds,
    value findCfgBlock,
    type BlockEdges(..),
    type DominanceIndex(..),
    value blockEdgesOf,
    value edgesAt,
    value inBlockSet,
    value immediateDominatorsOf,
    value dominanceIndexOf,
    value dominatesWithin,
)

type IrCfgBlock =
    | blockStart: Int
    | blockEnd: Int
    | blockSuccessors: List(Int)
    | blockPredecessors: List(Int)

let recursive listAt items index =
    match items with
        | [] -> None
        | head :: rest ->
            if index == 0
            then Some(head)
            else listAt(rest)(index - 1)

let recursive containsInt (item: Int) (items: List(Int)) =
    match items with
        | [] -> false
        | head :: rest ->
            if head == item
            then true
            else containsInt(item)(rest)

let recursive sortedSetContains (item: Int) (set: List(Int)) =
    match set with
        | [] -> false
        | head :: rest ->
            if head == item
            then true
            else
                if head > item
                then false
                else sortedSetContains(item)(rest)

let recursive sortedSetInsert (item: Int) (set: List(Int)) =
    match set with
        | [] -> [item]
        | head :: rest ->
            if head == item
            then set
            else
                if head > item
                then item :: set
                else head :: sortedSetInsert(item)(rest)

let appendUnique (item: Int) (items: List(Int)) =
    if containsInt(item)(items)
    then items
    else append(items)([item])

let isTerminator (kind: IrInstructionKind) =
    match kind with
        | Jump(_target) -> true
        | JumpIfFalse(_condition, _target) -> true
        | SwitchTag(_scrutinee, _cases, _default) -> true
        | Return(_source) -> true
        | _ -> false

let isLabel (kind: IrInstructionKind) =
    match kind with
        | Label(_name) -> true
        | _ -> false

// Block starts are discovered in ascending order, so they accumulate newest-first and `lastStart`
// stands in for the set's duplicate check: a label right after a terminator is the start that
// terminator already opened. The list never leaves this loop. A helper that took it and handed it
// back, changed or not, would have to return an owned list, and for a parameter whose ownership it
// cannot see that means copying the whole list once per instruction.
let recursive collectBlockStartsNewestFirst (instructions: List(IrInstruction)) (index: Int) (count: Int) (lastStart: Int) (newestFirst: List(Int)) =
    match instructions with
        | [] -> newestFirst
        | IrInstruction { instruction = kind } :: rest ->
            if isLabel(kind) && index > lastStart
            then collectBlockStartsNewestFirst(rest)(index + 1)(count)(index)(index :: newestFirst)
            else
                if isTerminator(kind) && index + 1 < count
                then collectBlockStartsNewestFirst(rest)(index + 1)(count)(index + 1)(index + 1 :: newestFirst)
                else collectBlockStartsNewestFirst(rest)(index + 1)(count)(lastStart)(newestFirst)

let collectBlockStarts (instructions: List(IrInstruction)) (count: Int) =
    [0]
    |> collectBlockStartsNewestFirst(instructions)(0)(count)(0)
    |> reverse

let recursive blockSpans (starts: List(Int)) (count: Int) =
    match starts with
        | [] -> []
        | start :: rest ->
            match rest with
                | next :: _ -> (start, next) :: blockSpans(rest)(count)
                | [] -> [(start, count)]

// The block index of every label that opens a block, found in one walk of the instructions along
// the spans. A label that opens two blocks names the first.
let recursive labelBlocksInto (instructions: List(IrInstruction)) (position: Int) (spans: List((Int, Int))) (index: Int) (labels: MapTree(Str, Int)) =
    match spans with
        | [] -> labels
        | (start, end) :: restSpans ->
            match instructions with
                | [] -> labels
                | IrInstruction { instruction = kind } :: rest ->
                    if position >= end
                    then labelBlocksInto(instructions)(position)(restSpans)(index + 1)(labels)
                    else
                        if position == start
                        then
                            match kind with
                                | Label(name) ->
                                    match Ashes.Collection.Map.getStr(name)(labels) with
                                        | Some(_first) -> labelBlocksInto(rest)(position + 1)(spans)(index)(labels)
                                        | None ->
                                            labels
                                            |> Ashes.Collection.Map.setStr(name)(index)
                                            |> labelBlocksInto(rest)(position + 1)(spans)(index)
                                | _ -> labelBlocksInto(rest)(position + 1)(spans)(index)(labels)
                        else labelBlocksInto(rest)(position + 1)(spans)(index)(labels)

let labelBlock (name: Str) (labels: MapTree(Str, Int)) =
    match Ashes.Collection.Map.getStr(name)(labels) with
        | Some(index) -> index
        | None -> -1

let recursive caseTargets (cases: List(IrSwitchCase)) (labels: MapTree(Str, Int)) (acc: List(Int)) =
    match cases with
        | [] -> acc
        | IrSwitchCase { label = label } :: rest ->
            acc
            |> appendUnique(labelBlock(label)(labels))
            |> caseTargets(rest)(labels)

let recursive appendAllUnique (items: List(Int)) (acc: List(Int)) =
    match items with
        | [] -> acc
        | head :: rest ->
            acc
            |> appendUnique(head)
            |> appendAllUnique(rest)

let fallThroughOf (blockCount: Int) (index: Int) =
    if index + 1 < blockCount
    then [index + 1]
    else []

// The successors of block `index`, read off the last instruction of its span.
let blockSuccessorsAfter (kind: IrInstructionKind) (labels: MapTree(Str, Int)) (blockCount: Int) (index: Int) =
    match kind with
        | Jump(target) -> [labelBlock(target)(labels)]
        | JumpIfFalse(_condition, target) ->
            appendAllUnique(fallThroughOf(blockCount)(index))([labelBlock(target)(labels)])
        | SwitchTag(_scrutinee, cases, default) ->
            []
            |> caseTargets(cases)(labels)
            |> appendUnique(labelBlock(default)(labels))
        | Return(_source) -> []
        | _ -> fallThroughOf(blockCount)(index)

// Every block's successors, in one walk of the instructions along the spans.
let recursive indexedSuccessors (instructions: List(IrInstruction)) (position: Int) (labels: MapTree(Str, Int)) (blockCount: Int) (spans: List((Int, Int))) (index: Int) =
    match spans with
        | [] -> []
        | (_start, end) :: restSpans ->
            match instructions with
                | [] -> (index, fallThroughOf(blockCount)(index)) :: indexedSuccessors([])(position)(labels)(blockCount)(restSpans)(index + 1)
                | IrInstruction { instruction = kind } :: rest ->
                    if position == end - 1
                    then (index, blockSuccessorsAfter(kind)(labels)(blockCount)(index)) :: indexedSuccessors(rest)(position + 1)(labels)(blockCount)(restSpans)(index + 1)
                    else indexedSuccessors(rest)(position + 1)(labels)(blockCount)(spans)(index)

// Every edge as a (target, source) pair. One flat self-recursive walk over both the remaining
// groups and the current group's remaining targets (rather than one recursive function calling
// another to extend the same accumulator) avoids a stage-0 codegen bug on that shape when the
// accumulator is a List of tuples.
let recursive edgesOf (groups: List((Int, List(Int)))) (currentFrom: Int) (currentTargets: List(Int)) (acc: List((Int, Int))) =
    match currentTargets with
        | target :: rest -> edgesOf(groups)(currentFrom)(rest)((target, currentFrom) :: acc)
        | [] ->
            match groups with
                | [] -> acc
                | (from, targets) :: rest -> edgesOf(rest)(from)(targets)(acc)

// Orders the edges by target, and for one target by source, latest first.
let edgeBefore (left: (Int, Int)) (right: (Int, Int)) =
    match (left, right) with
        | ((leftTarget, leftFrom), (rightTarget, rightFrom)) -> leftTarget < rightTarget || leftTarget == rightTarget && leftFrom >= rightFrom

// The predecessors of every block from `index` up, each in ascending order, out of the ordered
// edges: one flat walk that conses a block's sources as it passes them and closes the block at the
// first edge into a later one. An edge into no block (an unknown label) is passed over.
let recursive predecessorsByBlock (edges: List((Int, Int))) (index: Int) (blockCount: Int) (current: List(Int)) (finished: List(List(Int))) =
    if index >= blockCount
    then reverse(finished)
    else
        match edges with
            | [] -> predecessorsByBlock([])(index + 1)(blockCount)([])(current :: finished)
            | (target, from) :: rest ->
                if target < index
                then predecessorsByBlock(rest)(index)(blockCount)(current)(finished)
                else
                    if target == index
                    then predecessorsByBlock(rest)(index)(blockCount)(from :: current)(finished)
                    else predecessorsByBlock(edges)(index + 1)(blockCount)([])(current :: finished)

let recursive assembleAllBlocks (spans: List((Int, Int))) (predecessors: List(List(Int))) (remaining: List((Int, List(Int)))) =
    match (spans, predecessors, remaining) with
        | ((start, end) :: spanRest, sources :: predecessorRest, (_index, targets) :: successorRest) -> IrCfgBlock(blockStart = start, blockEnd = end, blockSuccessors = targets, blockPredecessors = sources) :: assembleAllBlocks(spanRest)(predecessorRest)(successorRest)
        | _ -> []

let buildCfgBlocks (instructions: List(IrInstruction)) =
    instructions
    |> length
    |> (given (count) ->
        count
        |> blockSpans(collectBlockStarts(instructions)(count))
        |> (given (spans) ->
            0
            |> indexedSuccessors(instructions)(0)(labelBlocksInto(instructions)(0)(spans)(0)(Ashes.Collection.Map.empty))(length(spans))(spans)
            |> (given (successors) ->
                assembleAllBlocks(spans)(predecessorsByBlock([]
                |> edgesOf(successors)(0)([])
                |> sortBy(edgeBefore))(0)(length(spans))([])([]))(successors))))

// Per block, whether it opens with a label and whether it ends in a terminator, in one walk of the
// instructions along the blocks.
let recursive blockBoundaryKindsFrom (instructions: List(IrInstruction)) (position: Int) (blocks: List(IrCfgBlock)) (opensWithLabel: Bool) =
    match blocks with
        | [] -> []
        | IrCfgBlock { blockStart = start, blockEnd = end } :: restBlocks ->
            match instructions with
                | [] -> (opensWithLabel, false) :: blockBoundaryKindsFrom([])(position)(restBlocks)(false)
                | IrInstruction { instruction = kind } :: rest ->
                    let opens =
                        if position == start
                        then isLabel(kind)
                        else opensWithLabel
                    in
                        if position == end - 1
                        then (opens, isTerminator(kind)) :: blockBoundaryKindsFrom(rest)(position + 1)(restBlocks)(false)
                        else blockBoundaryKindsFrom(rest)(position + 1)(blocks)(opens)

let blockBoundaryKinds (instructions: List(IrInstruction)) (blocks: List(IrCfgBlock)) = blockBoundaryKindsFrom(instructions)(0)(blocks)(false)

let recursive findCfgBlockFrom (blocks: List(IrCfgBlock)) (instructionIndex: Int) (index: Int) =
    match blocks with
        | [] -> -1
        | IrCfgBlock { blockStart = start, blockEnd = end } :: rest ->
            if instructionIndex >= start && instructionIndex < end
            then index
            else findCfgBlockFrom(rest)(instructionIndex)(index + 1)

let findCfgBlock (blocks: List(IrCfgBlock)) (instructionIndex: Int) = findCfgBlockFrom(blocks)(instructionIndex)(0)

// A graph's edges by block index, for the walks that follow them from block to block.
type BlockEdges =
    | successorsOfBlock: MapTree(Int, List(Int))
    | predecessorsOfBlock: MapTree(Int, List(Int))

let recursive blockEdgesInto (blocks: List(IrCfgBlock)) (index: Int) (successors: MapTree(Int, List(Int))) (predecessors: MapTree(Int, List(Int))) =
    match blocks with
        | [] -> BlockEdges(successorsOfBlock = successors, predecessorsOfBlock = predecessors)
        | IrCfgBlock { blockSuccessors = targets, blockPredecessors = sources } :: rest ->
            predecessors
            |> Ashes.Collection.Map.setInt(index)(sources)
            |> blockEdgesInto(rest)(index + 1)(Ashes.Collection.Map.setInt(index)(targets)(successors))

let blockEdgesOf (blocks: List(IrCfgBlock)) = blockEdgesInto(blocks)(0)(Ashes.Collection.Map.empty)(Ashes.Collection.Map.empty)

let edgesAt (index: Int) (edges: MapTree(Int, List(Int))) =
    match Ashes.Collection.Map.getInt(index)(edges) with
        | Some(blocks) -> blocks
        | None -> []

let inBlockSet (block: Int) (set: MapTree(Int, Bool)) =
    match Ashes.Collection.Map.getInt(block)(set) with
        | Some(_listed) -> true
        | None -> false

let blockNumberAt (block: Int) (numbers: MapTree(Int, Int)) =
    match Ashes.Collection.Map.getInt(block)(numbers) with
        | Some(number) -> number
        | None -> -1

// The reverse postorder of the blocks reachable from the entry: a depth-first walk over an
// explicit stack, kept as two parallel lists (the open blocks and, for each, the successors still
// to visit), that conses a block onto the order once every successor is finished, so the entry
// ends up first and every block follows the block the walk reached it through.
let recursive reversePostorderOver (successors: MapTree(Int, List(Int))) (open: List(Int)) (pending: List(List(Int))) (visited: MapTree(Int, Bool)) (order: List(Int)) =
    match (open, pending) with
        | (block :: openRest, [] :: pendingRest) -> reversePostorderOver(successors)(openRest)(pendingRest)(visited)(block :: order)
        | (block :: openRest, (successor :: more) :: pendingRest) ->
            if inBlockSet(successor)(visited)
            then reversePostorderOver(successors)(block :: openRest)(more :: pendingRest)(visited)(order)
            else
                reversePostorderOver(successors)(successor :: block :: openRest)(edgesAt(successor)(successors) :: more :: pendingRest)(Ashes.Collection.Map.setInt(successor)(true)(visited))(order)
        | _ -> order

let recursive orderPositionsInto (order: List(Int)) (position: Int) (positions: MapTree(Int, Int)) =
    match order with
        | [] -> positions
        | block :: rest ->
            positions
            |> Ashes.Collection.Map.setInt(block)(position)
            |> orderPositionsInto(rest)(position + 1)

// The nearest common ancestor of two blocks in the immediate-dominator tree so far, found by
// walking whichever finger sits later in the reverse postorder up to its immediate dominator
// until the fingers meet.
let recursive intersectDominators (positions: MapTree(Int, Int)) (idoms: MapTree(Int, Int)) (left: Int) (right: Int) =
    if left == right
    then left
    else
        if blockNumberAt(left)(positions) > blockNumberAt(right)(positions)
        then
            intersectDominators(positions)(idoms)(blockNumberAt(left)(idoms))(right)
        else
            idoms
            |> blockNumberAt(right)
            |> intersectDominators(positions)(idoms)(left)

// The immediate dominator a block's predecessors agree on this round: every predecessor that
// already has one (a reachable block processed earlier in the order, or its own earlier value)
// takes part, and an unreachable or not yet processed predecessor is skipped.
let recursive dominatorCandidate (positions: MapTree(Int, Int)) (idoms: MapTree(Int, Int)) (predecessors: List(Int)) (current: Int) =
    match predecessors with
        | [] -> current
        | predecessor :: rest ->
            if blockNumberAt(predecessor)(idoms) < 0
            then dominatorCandidate(positions)(idoms)(rest)(current)
            else
                if current < 0
                then dominatorCandidate(positions)(idoms)(rest)(predecessor)
                else
                    current
                    |> intersectDominators(positions)(idoms)(predecessor)
                    |> dominatorCandidate(positions)(idoms)(rest)

// One round over the reverse postorder; the count of blocks whose immediate dominator it changed
// comes back with the table.
let recursive dominatorRound (predecessors: MapTree(Int, List(Int))) (blockCount: Int) (order: List(Int)) (positions: MapTree(Int, Int)) (idoms: MapTree(Int, Int)) (changed: Int) =
    match order with
        | [] -> (idoms, changed)
        | block :: rest ->
            if block == 0
            then dominatorRound(predecessors)(blockCount)(rest)(positions)(idoms)(changed)
            else
                let candidate =
                    dominatorCandidate(positions)(idoms)(edgesAt(block)(predecessors))(-1)
                in
                    if candidate < 0 || candidate == blockNumberAt(block)(idoms) || block < 0 || block >= blockCount
                    then dominatorRound(predecessors)(blockCount)(rest)(positions)(idoms)(changed)
                    else
                        dominatorRound(predecessors)(blockCount)(rest)(positions)(Ashes.Collection.Map.setInt(block)(candidate)(idoms))(changed + 1)

let recursive dominatorFixpoint (predecessors: MapTree(Int, List(Int))) (blockCount: Int) (order: List(Int)) (positions: MapTree(Int, Int)) (idoms: MapTree(Int, Int)) =
    match dominatorRound(predecessors)(blockCount)(order)(positions)(idoms)(0) with
        | (next, changed) ->
            if changed == 0
            then next
            else dominatorFixpoint(predecessors)(blockCount)(order)(positions)(next)

let recursive dominatorList (idoms: MapTree(Int, Int)) (index: Int) (acc: List(Int)) =
    if index < 0
    then acc
    else dominatorList(idoms)(index - 1)(blockNumberAt(index)(idoms) :: acc)

// The immediate dominator of every block, indexed by block: the entry is its own, an unreachable
// block has none (-1), and every other block's is refined over the reverse postorder until no
// block changes. The table is worked out in maps and handed back as one Int per block.
let immediateDominatorsOf (blocks: List(IrCfgBlock)) (edges: BlockEdges) =
    (let blockCount = length(blocks)
    in
        let order =
            reversePostorderOver(edges.successorsOfBlock)([0])([edgesAt(0)(edges.successorsOfBlock)])(Ashes.Collection.Map.setInt(0)(true)(Ashes.Collection.Map.empty))([])
        in
            if blockCount <= 0
            then []
            else
                dominatorList(Ashes.Collection.Map.empty
                |> Ashes.Collection.Map.setInt(0)(0)
                |> dominatorFixpoint(edges.predecessorsOfBlock)(blockCount)(order)(orderPositionsInto(order)(0)(Ashes.Collection.Map.empty)))(blockCount - 1)([]))

// The dominator tree numbered by a depth-first walk: a block dominates another exactly when the
// other's visit falls inside its own.
type DominanceIndex =
    | dominanceEnter: MapTree(Int, Int)
    | dominanceLeave: MapTree(Int, Int)

let recursive dominatorChildrenInto (idoms: List(Int)) (block: Int) (children: MapTree(Int, List(Int))) (roots: List(Int)) =
    match idoms with
        | [] -> (children, roots)
        | parent :: rest ->
            if parent < 0 || parent == block
            then dominatorChildrenInto(rest)(block + 1)(children)(block :: roots)
            else
                dominatorChildrenInto(rest)(block + 1)(Ashes.Collection.Map.setInt(parent)(block :: edgesAt(parent)(children))(children))(roots)

let recursive numberDominatorTree (children: MapTree(Int, List(Int))) (roots: List(Int)) (open: List(Int)) (pending: List(List(Int))) (clock: Int) (enter: MapTree(Int, Int)) (leave: MapTree(Int, Int)) =
    match (open, pending) with
        | (block :: openRest, [] :: pendingRest) ->
            leave
            |> Ashes.Collection.Map.setInt(block)(clock)
            |> numberDominatorTree(children)(roots)(openRest)(pendingRest)(clock + 1)(enter)
        | (block :: openRest, (child :: more) :: pendingRest) ->
            numberDominatorTree(children)(roots)(child :: block :: openRest)(edgesAt(child)(children) :: more :: pendingRest)(clock + 1)(Ashes.Collection.Map.setInt(child)(clock)(enter))(leave)
        | _ ->
            match roots with
                | [] -> DominanceIndex(dominanceEnter = enter, dominanceLeave = leave)
                | root :: rest ->
                    numberDominatorTree(children)(rest)([root])([edgesAt(root)(children)])(clock + 1)(Ashes.Collection.Map.setInt(root)(clock)(enter))(leave)

let dominanceIndexOf (idoms: List(Int)) =
    match dominatorChildrenInto(idoms)(0)(Ashes.Collection.Map.empty)([]) with
        | (children, roots) -> numberDominatorTree(children)(roots)([])([])(0)(Ashes.Collection.Map.empty)(Ashes.Collection.Map.empty)

// Whether `dominator` dominates `block`: the block itself, or a block on its immediate-dominator
// chain up to the entry. An unreachable block is dominated only by itself.
let dominatesWithin (index: DominanceIndex) (dominator: Int) (block: Int) =
    if block == dominator
    then true
    else
        let dominatorEnter = blockNumberAt(dominator)(index.dominanceEnter)
        in
            let blockEnter = blockNumberAt(block)(index.dominanceEnter)
            in dominatorEnter >= 0 && blockEnter >= 0 && dominatorEnter < blockEnter && blockNumberAt(block)(index.dominanceLeave) < blockNumberAt(dominator)(index.dominanceLeave)
