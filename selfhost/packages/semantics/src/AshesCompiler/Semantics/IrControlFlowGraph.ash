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
import AshesCompiler.Semantics.IrInstructions
export (
    type IrCfgBlock(..),
    value listAt,
    value containsInt,
    value sortedSetContains,
    value sortedSetInsert,
    value buildCfgBlocks,
    value findCfgBlock,
    value computeImmediateDominators,
    value dominates,
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

let recursive collectBlockStarts (instructions: List(IrInstruction)) (index: Int) (count: Int) (starts: List(Int)) =
    match instructions with
        | [] -> starts
        | IrInstruction { instruction = kind } :: rest ->
            starts
            |> (given (current) ->
                match kind with
                    | Label(_name) -> sortedSetInsert(index)(current)
                    | _ -> current)
            |> (given (current) ->
                if isTerminator(kind) && index + 1 < count
                then sortedSetInsert(index + 1)(current)
                else current)
            |> collectBlockStarts(rest)(index + 1)(count)

let recursive blockSpans (starts: List(Int)) (count: Int) =
    match starts with
        | [] -> []
        | start :: rest ->
            match rest with
                | next :: _ -> (start, next) :: blockSpans(rest)(count)
                | [] -> [(start, count)]

// `(label, block index)` for every block that opens with a label.
let recursive labelBlocks (instructions: List(IrInstruction)) (spans: List((Int, Int))) (index: Int) =
    match spans with
        | [] -> []
        | (start, _end) :: rest ->
            match listAt(instructions)(start) with
                | Some(IrInstruction { instruction = Label(name) }) -> (name, index) :: labelBlocks(instructions)(rest)(index + 1)
                | _ -> labelBlocks(instructions)(rest)(index + 1)

let recursive labelBlock (name: Str) (labels: List((Str, Int))) =
    match labels with
        | [] -> -1
        | (candidate, index) :: rest ->
            if candidate == name
            then index
            else labelBlock(name)(rest)

let recursive caseTargets (cases: List(IrSwitchCase)) (labels: List((Str, Int))) (acc: List(Int)) =
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

// The successors of block `index`, read off the last instruction of its span.
let blockSuccessorsOf (instructions: List(IrInstruction)) (labels: List((Str, Int))) (blockCount: Int) (index: Int) (span: (Int, Int)) =
    match span with
        | (_start, end) ->
            ((given (fallThrough) ->
                match listAt(instructions)(end - 1) with
                    | Some(IrInstruction { instruction = Jump(target) }) -> [labelBlock(target)(labels)]
                    | Some(IrInstruction { instruction = JumpIfFalse(_condition, target) }) -> appendAllUnique(fallThrough)([labelBlock(target)(labels)])
                    | Some(IrInstruction { instruction = SwitchTag(_scrutinee, cases, default) }) ->
                        []
                        |> caseTargets(cases)(labels)
                        |> appendUnique(labelBlock(default)(labels))
                    | Some(IrInstruction { instruction = Return(_source) }) -> []
                    | _ -> fallThrough))(if index + 1 < blockCount
            then [index + 1]
            else [])

let recursive indexedSuccessors (instructions: List(IrInstruction)) (labels: List((Str, Int))) (blockCount: Int) (spans: List((Int, Int))) (index: Int) =
    match spans with
        | [] -> []
        | span :: rest -> (index, blockSuccessorsOf(instructions)(labels)(blockCount)(index)(span)) :: indexedSuccessors(instructions)(labels)(blockCount)(rest)(index + 1)

// Every edge as a (target, source) pair, gathered once so a block's predecessors come from a scan
// over plain integers rather than from a membership test against each block's successor list.
// One flat self-recursive walk over both the remaining groups and the current group's remaining
// targets (rather than one recursive function calling another to extend the same accumulator)
// avoids a stage-0 codegen bug on that shape when the accumulator is a List of tuples.
let recursive edgesOf (groups: List((Int, List(Int)))) (currentFrom: Int) (currentTargets: List(Int)) (acc: List((Int, Int))) =
    match currentTargets with
        | target :: rest -> edgesOf(groups)(currentFrom)(rest)((target, currentFrom) :: acc)
        | [] ->
            match groups with
                | [] -> acc
                | (from, targets) :: rest -> edgesOf(rest)(from)(targets)(acc)

let recursive predecessorsFromEdges (index: Int) (edges: List((Int, Int))) (acc: List(Int)) =
    match edges with
        | [] -> acc
        | (target, from) :: rest ->
            if target == index
            then predecessorsFromEdges(index)(rest)(from :: acc)
            else predecessorsFromEdges(index)(rest)(acc)

let recursive assembleAllBlocks (spans: List((Int, Int))) (edges: List((Int, Int))) (remaining: List((Int, List(Int)))) (index: Int) =
    match (spans, remaining) with
        | ((start, end) :: spanRest, (_index, targets) :: successorRest) -> IrCfgBlock(blockStart = start, blockEnd = end, blockSuccessors = targets, blockPredecessors = predecessorsFromEdges(index)(edges)([])) :: assembleAllBlocks(spanRest)(edges)(successorRest)(index + 1)
        | _ -> []

let buildCfgBlocks (instructions: List(IrInstruction)) =
    instructions
    |> length
    |> (given (count) ->
        count
        |> blockSpans(collectBlockStarts(instructions)(0)(count)([0]))
        |> (given (spans) ->
            0
            |> indexedSuccessors(instructions)(labelBlocks(instructions)(spans)(0))(length(spans))(spans)
            |> (given (successors) ->
                assembleAllBlocks(spans)(edgesOf(successors)(0)([])([]))(successors)(0))))

let recursive findCfgBlockFrom (blocks: List(IrCfgBlock)) (instructionIndex: Int) (index: Int) =
    match blocks with
        | [] -> -1
        | IrCfgBlock { blockStart = start, blockEnd = end } :: rest ->
            if instructionIndex >= start && instructionIndex < end
            then index
            else findCfgBlockFrom(rest)(instructionIndex)(index + 1)

let findCfgBlock (blocks: List(IrCfgBlock)) (instructionIndex: Int) = findCfgBlockFrom(blocks)(instructionIndex)(0)

let blockSuccessorsOfIndex (blocks: List(IrCfgBlock)) (index: Int) =
    match listAt(blocks)(index) with
        | Some(IrCfgBlock { blockSuccessors = successors }) -> successors
        | None -> []

let blockPredecessorsOfIndex (blocks: List(IrCfgBlock)) (index: Int) =
    match listAt(blocks)(index) with
        | Some(IrCfgBlock { blockPredecessors = predecessors }) -> predecessors
        | None -> []

// The reverse postorder of the blocks reachable from the entry: a depth-first walk over an
// explicit stack, kept as two parallel lists (the open blocks and, for each, the successors still
// to visit), that conses a block onto the order once every successor is finished, so the entry
// ends up first and every block follows the block the walk reached it through.
let recursive reversePostorder (blocks: List(IrCfgBlock)) (open: List(Int)) (pending: List(List(Int))) (visited: List(Int)) (order: List(Int)) =
    match (open, pending) with
        | (block :: openRest, [] :: pendingRest) -> reversePostorder(blocks)(openRest)(pendingRest)(visited)(block :: order)
        | (block :: openRest, (successor :: more) :: pendingRest) ->
            if sortedSetContains(successor)(visited)
            then reversePostorder(blocks)(block :: openRest)(more :: pendingRest)(visited)(order)
            else
                reversePostorder(blocks)(successor :: block :: openRest)(blockSuccessorsOfIndex(blocks)(successor) :: more :: pendingRest)(sortedSetInsert(successor)(visited))(order)
        | _ -> order

let recursive orderPositions (order: List(Int)) (position: Int) (acc: List((Int, Int))) =
    match order with
        | [] -> acc
        | block :: rest -> orderPositions(rest)(position + 1)((block, position) :: acc)

let recursive orderPosition (positions: List((Int, Int))) (block: Int) =
    match positions with
        | [] -> -1
        | (candidate, position) :: rest ->
            if candidate == block
            then position
            else orderPosition(rest)(block)

let idomAt (idoms: List(Int)) (block: Int) =
    match listAt(idoms)(block) with
        | Some(idom) -> idom
        | None -> -1

let recursive replaceIntAt (items: List(Int)) (index: Int) (replacement: Int) =
    match items with
        | [] -> []
        | head :: rest ->
            if index == 0
            then replacement :: rest
            else head :: replaceIntAt(rest)(index - 1)(replacement)

let recursive noIdoms (count: Int) (acc: List(Int)) =
    if count <= 0
    then acc
    else noIdoms(count - 1)(-1 :: acc)

// The nearest common ancestor of two blocks in the immediate-dominator tree so far, found by
// walking whichever finger sits later in the reverse postorder up to its immediate dominator
// until the fingers meet.
let recursive intersectIdoms (positions: List((Int, Int))) (idoms: List(Int)) (left: Int) (right: Int) =
    if left == right
    then left
    else
        if orderPosition(positions)(left) > orderPosition(positions)(right)
        then
            intersectIdoms(positions)(idoms)(idomAt(idoms)(left))(right)
        else
            right
            |> idomAt(idoms)
            |> intersectIdoms(positions)(idoms)(left)

// The immediate dominator a block's predecessors agree on this round: every predecessor that
// already has one (a reachable block processed earlier in the order, or its own earlier value)
// takes part, and an unreachable or not yet processed predecessor is skipped.
let recursive idomCandidate (positions: List((Int, Int))) (idoms: List(Int)) (predecessors: List(Int)) (current: Int) =
    match predecessors with
        | [] -> current
        | predecessor :: rest ->
            if idomAt(idoms)(predecessor) < 0
            then idomCandidate(positions)(idoms)(rest)(current)
            else
                if current < 0
                then idomCandidate(positions)(idoms)(rest)(predecessor)
                else
                    current
                    |> intersectIdoms(positions)(idoms)(predecessor)
                    |> idomCandidate(positions)(idoms)(rest)

let recursive idomRound (blocks: List(IrCfgBlock)) (order: List(Int)) (positions: List((Int, Int))) (idoms: List(Int)) =
    match order with
        | [] -> idoms
        | block :: rest ->
            if block == 0
            then idomRound(blocks)(rest)(positions)(idoms)
            else
                let candidate =
                    idomCandidate(positions)(idoms)(blockPredecessorsOfIndex(blocks)(block))(-1)
                in
                    if candidate < 0 || candidate == idomAt(idoms)(block)
                    then idomRound(blocks)(rest)(positions)(idoms)
                    else
                        candidate
                        |> replaceIntAt(idoms)(block)
                        |> idomRound(blocks)(rest)(positions)

let recursive idomFixpoint (blocks: List(IrCfgBlock)) (order: List(Int)) (positions: List((Int, Int))) (idoms: List(Int)) =
    (let next = idomRound(blocks)(order)(positions)(idoms)
    in
        if next == idoms
        then next
        else idomFixpoint(blocks)(order)(positions)(next))

// The immediate dominator of every block, indexed by block: the entry is its own, an unreachable
// block has none (-1), and every other block's is refined over the reverse postorder until no
// block changes. Dominance itself is then the walk up this tree (`dominates`), so the whole table
// is one Int per block rather than a set per block.
let computeImmediateDominators (blocks: List(IrCfgBlock)) =
    (let order = reversePostorder(blocks)([0])([blockSuccessorsOfIndex(blocks)(0)])([0])([])
    in
        let positions = orderPositions(order)(0)([])
        in
            0
            |> replaceIntAt(noIdoms(length(blocks))([]))(0)
            |> idomFixpoint(blocks)(order)(positions))

// Whether `dominator` dominates `block`: the block itself, or a block on its immediate-dominator
// chain up to the entry. An unreachable block is dominated only by itself, as its empty chain says.
let recursive dominates (idoms: List(Int)) (dominator: Int) (block: Int) =
    if block == dominator
    then true
    else
        let parent = idomAt(idoms)(block)
        in
            if parent < 0 || parent == block
            then false
            else dominates(idoms)(dominator)(parent)
