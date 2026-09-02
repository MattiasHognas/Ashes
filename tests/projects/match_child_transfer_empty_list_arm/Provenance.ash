export (
    type BytesOwnershipProvenance(..),
    type FunctionResultProvenance(..),
    value buildProvenanceNode,
    value resolveResultProvenances,
)

type BytesOwnershipProvenance =
    | BytesProvenanceUnknown
    | BytesProvenanceFreshOwnedBuffer
    | BytesProvenanceBorrowedView

type FunctionResultProvenance =
    | rcEligible: Bool
    | forwardsTo: Maybe(Str)
    | bytesProvenance: BytesOwnershipProvenance

type ProvenanceFunctionNode =
    | functionName: Str
    | hasDirectEligibleResult: Bool
    | hasRejectedResult: Bool
    | consideredArmCount: Int
    | unambiguousForwardTarget: Maybe(Str)
    | directBytesProvenances: List(BytesOwnershipProvenance)
    | hasUnknownBytesResult: Bool

let recursive listContainsStr (list: List(Str)) (target: Str) =
    match list with
        | [] -> false
        | head :: tail ->
            if head == target
            then true
            else listContainsStr(tail)(target)

let recursive listContainsInt (list: List(Int)) (target: Int) =
    match list with
        | [] -> false
        | head :: tail ->
            if head == target
            then true
            else listContainsInt(tail)(target)

let buildProvenanceNode (name: Str) (hasDirect: Bool) (hasRejected: Bool) (armCount: Int) (unambiguousTarget: Maybe(Str)) (bytesProv: List(BytesOwnershipProvenance)) (unknownBytes: Bool) =
    ProvenanceFunctionNode(
        functionName = name,
        hasDirectEligibleResult = hasDirect,
        hasRejectedResult = hasRejected,
        consideredArmCount = armCount,
        unambiguousForwardTarget = unambiguousTarget,
        directBytesProvenances = bytesProv,
        hasUnknownBytesResult = unknownBytes
    )

let recursive getNodeNames (nodes: List(ProvenanceFunctionNode)) =
    match nodes with
        | [] -> []
        | head :: tail ->
            match head with
                | ProvenanceFunctionNode { functionName = name } -> name :: getNodeNames(tail)

let recursive getNodeFacts (nodes: List(ProvenanceFunctionNode)) =
    match nodes with
        | [] -> []
        | head :: tail ->
            match head with
                | ProvenanceFunctionNode { functionName = name, hasDirectEligibleResult = d, hasRejectedResult = r, consideredArmCount = a, directBytesProvenances = b, hasUnknownBytesResult = u } -> (name, d, r, a, b, u) :: getNodeFacts(tail)

let recursive getNodeUnambiguous (nodes: List(ProvenanceFunctionNode)) =
    match nodes with
        | [] -> []
        | head :: tail ->
            match head with
                | ProvenanceFunctionNode { functionName = name, unambiguousForwardTarget = unam } -> (name, unam) :: getNodeUnambiguous(tail)

let recursive findComponentIdOf (components: List(List(Str))) (func: Str) (currentId: Int) =
    match components with
        | [] -> -1
        | members :: tail ->
            if listContainsStr(members)(func)
            then currentId
            else findComponentIdOf(tail)(func)(currentId + 1)

let recursive lookupUnambiguous (map: List((Str, Maybe(Str)))) (key: Str) =
    match map with
        | [] -> None
        | pair :: rest ->
            match pair with
                | (k, v) ->
                    if k == key
                    then v
                    else lookupUnambiguous(rest)(key)

let recursive lookupDirectBytes (facts: List((Str, Bool, Bool, Int, List(BytesOwnershipProvenance), Bool))) (key: Str) =
    match facts with
        | [] -> []
        | fact :: rest ->
            match fact with
                | (name, _, _, _, b, _) ->
                    if name == key
                    then b
                    else lookupDirectBytes(rest)(key)

let recursive resolveNodeProvenances (names: List(Str)) (sccs: List(List(Str))) (eligibleCompIds: List(Int)) (facts: List((Str, Bool, Bool, Int, List(BytesOwnershipProvenance), Bool))) (unambiguousMap: List((Str, Maybe(Str)))) =
    match names with
        | [] -> []
        | name :: tail ->
            let compId = findComponentIdOf(sccs)(name)(0)
            in
                let isRc = listContainsInt(eligibleCompIds)(compId)
                in
                    let unambiguous = lookupUnambiguous(unambiguousMap)(name)
                    in
                        let directBytes = lookupDirectBytes(facts)(name)
                        in
                            let bProv =
                                match directBytes with
                                    | [] -> BytesProvenanceUnknown
                                    | single :: rest ->
                                        match rest with
                                            | [] -> single
                                            | _ -> BytesProvenanceUnknown
                            in
                                let prov =
                                    FunctionResultProvenance(
                                        rcEligible = isRc,
                                        forwardsTo = unambiguous,
                                        bytesProvenance = bProv
                                    )
                                in (name, prov) :: resolveNodeProvenances(tail)(sccs)(eligibleCompIds)(facts)(unambiguousMap)

let resolveResultProvenances (nodes: List(ProvenanceFunctionNode)) =
    (let names = getNodeNames(nodes)
    in
        let facts = getNodeFacts(nodes)
        in
            let unambiguousMap = getNodeUnambiguous(nodes)
            in resolveNodeProvenances(names)([names])([0])(facts)(unambiguousMap))
