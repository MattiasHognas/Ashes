// Basic blocks and dominators over one function's instruction list, stage 0's
// `IrControlFlowGraph`. A block starts at index 0, at every `Label`, and right after every
// terminator (`Jump`, `JumpIfFalse`, `SwitchTag`, `Return`); its edges follow its last
// instruction: a jump to its label, a conditional jump to its label and then the fall-through
// block, a switch to every case label and then the default, a return nowhere, and anything else
// the fall-through. Successor and predecessor lists keep edge discovery order with one entry per
// edge. A set of block indices is a sorted list without duplicates; a dominator set is one such
// set per block, indexed by block, computed as the maximal fixpoint over the blocks reachable
// from block 0 (an unreachable block is dominated only by itself).
import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.IrInstructions
export (
    type IrCfgBlock(..),
    value listAt,
    value containsInt,
    value sortedSetContains,
    value sortedSetInsert,
    value sortedSetIntersect,
    value buildCfgBlocks,
    value findCfgBlock,
    value computeDominators,
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

let recursive sortedSetIntersect (left: List(Int)) (right: List(Int)) =
    match (left, right) with
        | ([], _) -> []
        | (_, []) -> []
        | (l :: leftRest, r :: rightRest) ->
            if l == r
            then l :: sortedSetIntersect(leftRest)(rightRest)
            else
                if l < r
                then sortedSetIntersect(leftRest)(right)
                else sortedSetIntersect(left)(rightRest)

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

let predecessorsOf (index: Int) (successors: List((Int, List(Int)))) =
    successors
    |> filter(given (entry) ->
        match entry with
            | (_from, targets) -> containsInt(index)(targets))
    |> map(given (entry) ->
        match entry with
            | (from, _targets) -> from)

let recursive assembleBlocks (spans: List((Int, Int))) (successors: List((Int, List(Int)))) (index: Int) =
    match (spans, successors) with
        | ((start, end) :: spanRest, (_index, targets) :: successorRest) -> IrCfgBlock(blockStart = start, blockEnd = end, blockSuccessors = targets, blockPredecessors = predecessorsOf(index)(successors)) :: assembleBlocks(spanRest)(successorRest)(index + 1)
        | _ -> []

let recursive assembleAllBlocks (spans: List((Int, Int))) (successors: List((Int, List(Int)))) (remaining: List((Int, List(Int)))) (index: Int) =
    match (spans, remaining) with
        | ((start, end) :: spanRest, (_index, targets) :: successorRest) -> IrCfgBlock(blockStart = start, blockEnd = end, blockSuccessors = targets, blockPredecessors = predecessorsOf(index)(successors)) :: assembleAllBlocks(spanRest)(successors)(successorRest)(index + 1)
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
            |> (given (successors) -> assembleAllBlocks(spans)(successors)(successors)(0))))

let recursive findCfgBlockFrom (blocks: List(IrCfgBlock)) (instructionIndex: Int) (index: Int) =
    match blocks with
        | [] -> -1
        | IrCfgBlock { blockStart = start, blockEnd = end } :: rest ->
            if instructionIndex >= start && instructionIndex < end
            then index
            else findCfgBlockFrom(rest)(instructionIndex)(index + 1)

let findCfgBlock (blocks: List(IrCfgBlock)) (instructionIndex: Int) = findCfgBlockFrom(blocks)(instructionIndex)(0)

let recursive reachableBlocks (blocks: List(IrCfgBlock)) (pending: List(Int)) (visited: List(Int)) =
    match pending with
        | [] -> visited
        | current :: rest ->
            if sortedSetContains(current)(visited)
            then reachableBlocks(blocks)(rest)(visited)
            else
                match listAt(blocks)(current) with
                    | Some(IrCfgBlock { blockSuccessors = successors }) ->
                        visited
                        |> sortedSetInsert(current)
                        |> reachableBlocks(blocks)(append(successors)(rest))
                    | None -> reachableBlocks(blocks)(rest)(visited)

let recursive intersectDominators (dominators: List(List(Int))) (predecessors: List(Int)) (acc: Maybe(List(Int))) =
    match predecessors with
        | [] ->
            match acc with
                | Some(set) -> set
                | None -> []
        | predecessor :: rest ->
            match listAt(dominators)(predecessor) with
                | Some(set) ->
                    match acc with
                        | Some(current) ->
                            intersectDominators(dominators)(rest)(set
                            |> sortedSetIntersect(current)
                            |> Some)
                        | None -> intersectDominators(dominators)(rest)(Some(set))
                | None -> intersectDominators(dominators)(rest)(acc)

let recursive initialDominators (blocks: List(IrCfgBlock)) (reachable: List(Int)) (index: Int) =
    match blocks with
        | [] -> []
        | _block :: rest ->
            (if index == 0
            then [0]
            else
                if sortedSetContains(index)(reachable)
                then reachable
                else [index]) :: initialDominators(rest)(reachable)(index + 1)

let recursive dominatorStep (blocks: List(IrCfgBlock)) (reachable: List(Int)) (dominators: List(List(Int))) (index: Int) =
    match blocks with
        | [] -> []
        | IrCfgBlock { blockPredecessors = predecessors } :: rest ->
            (if index == 0
            then [0]
            else
                if sortedSetContains(index)(reachable)
                then
                    None
                    |> intersectDominators(dominators)(filter(given (predecessor) -> sortedSetContains(predecessor)(reachable))(predecessors))
                    |> sortedSetInsert(index)
                else [index]) :: dominatorStep(rest)(reachable)(dominators)(index + 1)

let recursive dominatorFixpoint (blocks: List(IrCfgBlock)) (reachable: List(Int)) (dominators: List(List(Int))) =
    0
    |> dominatorStep(blocks)(reachable)(dominators)
    |> (given (next) ->
        if next == dominators
        then next
        else dominatorFixpoint(blocks)(reachable)(next))

let computeDominators (blocks: List(IrCfgBlock)) =
    []
    |> reachableBlocks(blocks)([0])
    |> (given (reachable) ->
        0
        |> initialDominators(blocks)(reachable)
        |> dominatorFixpoint(blocks)(reachable))
