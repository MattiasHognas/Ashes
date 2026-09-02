// expect: 0:T:T 1:T:T 2:T:F 3:T:T 4:T:T 5:T:F 9:F:F
// A pipe stage lambda that conses its argument onto a recursive self-call keeps the argument alive:
// the caller must not release the record after the closure call, or the liveness fixpoint below reads
// freed entries and never converges.
import Ashes.Collection.List
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

type BlockFacts =
    | factsBlock: Int
    | factsLoads: List(Int)
    | factsUses: List(Int)

type BlockLiveness =
    | livenessBlock: Int
    | livenessIn: Bool
    | livenessOut: Bool
    deriving {Eq}

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

let recursive showLiveness (liveness: List(BlockLiveness)) =
    match liveness with
        | [] -> ""
        | BlockLiveness { livenessBlock = b, livenessIn = i, livenessOut = o } :: rest ->
            Ashes.Text.fromInt(b) + (if i
            then ":T"
            else ":F") + (if o
            then ":T "
            else ":F ") + showLiveness(rest)

let recursive fixLiveness (rounds: Int) (blocks: List(IrCfgBlock)) (region: List(Int)) (facts: List(BlockFacts)) (liveness: List(BlockLiveness)) =
    (let next = stepLiveness(blocks)(region)(facts)(liveness)(region)
    in
        if next == liveness || rounds > 20
        then next
        else fixLiveness(rounds + 1)(blocks)(region)(facts)(next))

let recursive initialLiveness (region: List(Int)) =
    match region with
        | [] -> []
        | blockIndex :: rest -> BlockLiveness(livenessBlock = blockIndex, livenessIn = false, livenessOut = false) :: initialLiveness(rest)

let block (start: Int) (successors: List(Int)) = IrCfgBlock(blockStart = start, blockEnd = start + 1, blockSuccessors = successors, blockPredecessors = [])

let blocks = [block(0)([3, 1]), block(1)([3, 2]), block(2)([9]), block(3)([4]), block(4)([7, 5]), block(5)([7, 6]), block(6)([9]), block(7)([8]), block(8)([9]), block(9)([])]

let facts = [BlockFacts(factsBlock = 0, factsLoads = [1], factsUses = [1]), BlockFacts(factsBlock = 1, factsLoads = [], factsUses = [5]), BlockFacts(factsBlock = 2, factsLoads = [], factsUses = [9]), BlockFacts(factsBlock = 3, factsLoads = [], factsUses = []), BlockFacts(factsBlock = 4, factsLoads = [], factsUses = [20]), BlockFacts(factsBlock = 5, factsLoads = [], factsUses = [24]), BlockFacts(factsBlock = 9, factsLoads = [], factsUses = [])]

let region = [0, 1, 2, 3, 4, 5, 9]

region
|> initialLiveness
|> fixLiveness(0)(blocks)(region)(facts)
|> showLiveness
|> Ashes.IO.print
