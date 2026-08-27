// Whole-program result provenance and forwarding SCC analysis.
//
// Invariants:
// - Direct constructor applications, tuples, list/record literals, string additions, and fresh
//   producers are RC-eligible.
// - Forwarding calls inherit their callee's provenance.
// - Strongly-connected components are solved as a least fixpoint: productive recursion with a fresh
//   base converges, while pure forwarding cycles and non-RC paths fail closed.

import AshesCompiler.Semantics.OwnershipSummary
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import Ashes.Collection.List.length
export (
    type ProvenanceFunctionNode(..),
    type ProvenanceComponent(..),
    value buildProvenanceNode,
    value computeStronglyConnectedComponents,
    value buildComponents,
    value solveRcEligibilityFixpoint,
    value resolveResultProvenances,
)

type ProvenanceFunctionNode =
    | functionName: Str
    | hasDirectEligibleResult: Bool
    | hasRejectedResult: Bool
    | consideredArmCount: Int
    | forwardTargets: List(Str)
    | unambiguousForwardTarget: Maybe(Str)
    | directBytesProvenances: List(BytesOwnershipProvenance)
    | hasUnknownBytesResult: Bool
    deriving {Eq, Show}

type ProvenanceComponent =
    | componentId: Int
    | memberFunctions: List(Str)
    | hasDirectEligibleResult: Bool
    | hasRejectedResult: Bool
    | consideredArmCount: Int
    | dependencies: List(Int)
    | directBytesProvenances: List(BytesOwnershipProvenance)
    | hasUnknownBytesResult: Bool
    deriving {Eq, Show}

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

let recursive listContainsBytes (list: List(BytesOwnershipProvenance)) (target: BytesOwnershipProvenance) =
    match list with
        | [] -> false
        | head :: tail ->
            if head == target
            then true
            else listContainsBytes(tail)(target)

let recursive unionIntLists (a: List(Int)) (b: List(Int)) =
    match a with
        | [] -> b
        | head :: tail ->
            if listContainsInt(b)(head)
            then unionIntLists(tail)(b)
            else head :: unionIntLists(tail)(b)

let recursive unionBytesProvenances (listA: List(BytesOwnershipProvenance)) (listB: List(BytesOwnershipProvenance)) =
    match listB with
        | [] -> listA
        | head :: tail ->
            if listContainsBytes(listA)(head)
            then unionBytesProvenances(listA)(tail)
            else unionBytesProvenances(head :: listA)(tail)

let buildProvenanceNode (name: Str) (hasDirect: Bool) (hasRejected: Bool) (armCount: Int) (targets: List(Str)) (unambiguousTarget: Maybe(Str)) (bytesProv: List(BytesOwnershipProvenance)) (unknownBytes: Bool) =
    ProvenanceFunctionNode(
        functionName = name,
        hasDirectEligibleResult = hasDirect,
        hasRejectedResult = hasRejected,
        consideredArmCount = armCount,
        forwardTargets = targets,
        unambiguousForwardTarget = unambiguousTarget,
        directBytesProvenances = bytesProv,
        hasUnknownBytesResult = unknownBytes
    )

let recursive lookupStrList (map: List((Str, List(Str)))) (key: Str) =
    match map with
        | [] -> []
        | pair :: rest ->
            match pair with
                | (k, v) ->
                    if k == key
                    then v
                    else lookupStrList(rest)(key)

let recursive getNodeNames (nodes: List(ProvenanceFunctionNode)) =
    match nodes with
        | [] -> []
        | head :: tail ->
            match head with
                | ProvenanceFunctionNode { functionName = name } -> name :: getNodeNames(tail)

let recursive getNodeAdj (nodes: List(ProvenanceFunctionNode)) =
    match nodes with
        | [] -> []
        | head :: tail ->
            match head with
                | ProvenanceFunctionNode { functionName = name, forwardTargets = fwd } -> (name, fwd) :: getNodeAdj(tail)

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

// --- Graph operations & SCC via Kosaraju's algorithm ---
let recursive dfsPostOrder (worklist: List(Str)) (adj: List((Str, List(Str)))) (visited: List(Str)) (postOrder: List(Str)) =
    match worklist with
        | [] -> (visited, postOrder)
        | current :: rest ->
            if listContainsStr(visited)(current)
            then dfsPostOrder(rest)(adj)(visited)(postOrder)
            else
                let newVisited = current :: visited
                in
                    let targets = lookupStrList(adj)(current)
                    in
                        match dfsPostOrder(targets)(adj)(newVisited)(postOrder) with
                            | (vAfter, poAfter) -> dfsPostOrder(rest)(adj)(vAfter)(current :: poAfter)

let recursive computePostOrder (names: List(Str)) (adj: List((Str, List(Str)))) (visited: List(Str)) (postOrder: List(Str)) =
    match names with
        | [] -> postOrder
        | name :: rest ->
            match dfsPostOrder([name])(adj)(visited)(postOrder) with
                | (newVisited, newPo) -> computePostOrder(rest)(adj)(newVisited)(newPo)

let recursive collectPredecessors (adj: List((Str, List(Str)))) (target: Str) =
    match adj with
        | [] -> []
        | pair :: rest ->
            match pair with
                | (name, targets) ->
                    if listContainsStr(targets)(target)
                    then name :: collectPredecessors(rest)(target)
                    else collectPredecessors(rest)(target)

let recursive dfsReverse (worklist: List(Str)) (adj: List((Str, List(Str)))) (visited: List(Str)) (componentAcc: List(Str)) =
    match worklist with
        | [] -> (visited, componentAcc)
        | current :: rest ->
            if listContainsStr(visited)(current)
            then dfsReverse(rest)(adj)(visited)(componentAcc)
            else
                let newVisited = current :: visited
                in
                    let preds = collectPredecessors(adj)(current)
                    in
                        match dfsReverse(preds)(adj)(newVisited)(current :: componentAcc) with
                            | (vAfter, compAfter) -> dfsReverse(rest)(adj)(vAfter)(compAfter)

let recursive computeSccs (order: List(Str)) (adj: List((Str, List(Str)))) (visited: List(Str)) (components: List(List(Str))) =
    match order with
        | [] -> components
        | head :: tail ->
            if listContainsStr(visited)(head)
            then computeSccs(tail)(adj)(visited)(components)
            else
                match dfsReverse([head])(adj)(visited)([]) with
                    | (newVisited, comp) -> computeSccs(tail)(adj)(newVisited)(comp :: components)

let computeStronglyConnectedComponents (nodes: List(ProvenanceFunctionNode)) =
    (let names = getNodeNames(nodes)
    in
        let adj = getNodeAdj(nodes)
        in
            let postOrder = computePostOrder(names)(adj)([])([])
            in computeSccs(postOrder)(adj)([])([]))

// --- Component building and fixpoint solver ---
let recursive findComponentIdOf (components: List(List(Str))) (func: Str) (currentId: Int) =
    match components with
        | [] -> -1
        | members :: tail ->
            if listContainsStr(members)(func)
            then currentId
            else findComponentIdOf(tail)(func)(currentId + 1)

let recursive collectDependenciesFromTargets (targets: List(Str)) (components: List(List(Str))) (selfId: Int) =
    match targets with
        | [] -> []
        | target :: rest ->
            let targetCompId = findComponentIdOf(components)(target)(0)
            in
                let restDeps = collectDependenciesFromTargets(rest)(components)(selfId)
                in
                    if targetCompId >= 0
                    then
                        if targetCompId != selfId
                        then
                            if listContainsInt(restDeps)(targetCompId)
                            then restDeps
                            else targetCompId :: restDeps
                        else restDeps
                    else restDeps

let recursive collectComponentDependencies (members: List(Str)) (adj: List((Str, List(Str)))) (components: List(List(Str))) (selfId: Int) =
    match members with
        | [] -> []
        | func :: rest ->
            let targets = lookupStrList(adj)(func)
            in
                let deps = collectDependenciesFromTargets(targets)(components)(selfId)
                in
                    let restDeps = collectComponentDependencies(rest)(adj)(components)(selfId)
                    in unionIntLists(deps)(restDeps)

let recursive aggregateSingleFact (func: Str) (facts: List((Str, Bool, Bool, Int, List(BytesOwnershipProvenance), Bool))) =
    match facts with
        | [] -> (false, false, 0, [], false)
        | fact :: rest ->
            match fact with
                | (name, d, r, a, b, u) ->
                    if name == func
                    then (d, r, a, b, u)
                    else aggregateSingleFact(func)(rest)

let recursive aggregateComponentFacts (members: List(Str)) (facts: List((Str, Bool, Bool, Int, List(BytesOwnershipProvenance), Bool))) =
    match members with
        | [] -> (false, false, 0, [], false)
        | func :: rest ->
            match aggregateSingleFact(func)(facts) with
                | (d1, r1, a1, b1, u1) ->
                    match aggregateComponentFacts(rest)(facts) with
                        | (d2, r2, a2, b2, u2) ->
                            let d =
                                if d1
                                then true
                                else d2
                            in
                                let r =
                                    if r1
                                    then true
                                    else r2
                                in
                                    let a = a1 + a2
                                    in
                                        let b = unionBytesProvenances(b1)(b2)
                                        in
                                            let u =
                                                if u1
                                                then true
                                                else u2
                                            in (d, r, a, b, u)

let recursive buildComponentsAux (sccs: List(List(Str))) (allSccs: List(List(Str))) (adj: List((Str, List(Str)))) (facts: List((Str, Bool, Bool, Int, List(BytesOwnershipProvenance), Bool))) (currentId: Int) =
    match sccs with
        | [] -> []
        | members :: tail ->
            match aggregateComponentFacts(members)(facts) with
                | (direct, rejected, arms, bProv, unkBytes) ->
                    let deps = collectComponentDependencies(members)(adj)(allSccs)(currentId)
                    in
                        let compRejected =
                            if rejected
                            then true
                            else arms == 0
                        in
                            let comp =
                                ProvenanceComponent(
                                    componentId = currentId,
                                    memberFunctions = members,
                                    hasDirectEligibleResult = direct,
                                    hasRejectedResult = compRejected,
                                    consideredArmCount = arms,
                                    dependencies = deps,
                                    directBytesProvenances = bProv,
                                    hasUnknownBytesResult = unkBytes
                                )
                            in comp :: buildComponentsAux(tail)(allSccs)(adj)(facts)(currentId + 1)

let buildComponents (sccs: List(List(Str))) (nodes: List(ProvenanceFunctionNode)) =
    (let adj = getNodeAdj(nodes)
    in
        let facts = getNodeFacts(nodes)
        in buildComponentsAux(sccs)(sccs)(adj)(facts)(0))

let recursive allDependenciesEligible (deps: List(Int)) (eligible: List(Int)) =
    match deps with
        | [] -> true
        | depId :: rest ->
            if listContainsInt(eligible)(depId)
            then allDependenciesEligible(rest)(eligible)
            else false

let recursive anyDependencyEligible (deps: List(Int)) (eligible: List(Int)) =
    match deps with
        | [] -> false
        | depId :: rest ->
            if listContainsInt(eligible)(depId)
            then true
            else anyDependencyEligible(rest)(eligible)

let recursive solveRcEligibilityStep (comps: List(ProvenanceComponent)) (eligible: List(Int)) (changed: Bool) =
    match comps with
        | [] -> (eligible, changed)
        | comp :: rest ->
            match comp with
                | ProvenanceComponent { componentId = compId, hasDirectEligibleResult = hasDirect, hasRejectedResult = hasRejected, dependencies = deps } ->
                    if listContainsInt(eligible)(compId)
                    then solveRcEligibilityStep(rest)(eligible)(changed)
                    else
                        let grounded =
                            if hasDirect
                            then true
                            else anyDependencyEligible(deps)(eligible)
                        in
                            let allDepsOk = allDependenciesEligible(deps)(eligible)
                            in
                                if hasRejected
                                then solveRcEligibilityStep(rest)(eligible)(changed)
                                else
                                    if grounded
                                    then
                                        if allDepsOk
                                        then solveRcEligibilityStep(rest)(compId :: eligible)(true)
                                        else solveRcEligibilityStep(rest)(eligible)(changed)
                                    else solveRcEligibilityStep(rest)(eligible)(changed)

let recursive solveRcEligibilityFixpoint (comps: List(ProvenanceComponent)) (eligible: List(Int)) =
    match solveRcEligibilityStep(comps)(eligible)(false) with
        | (newEligible, changed) ->
            if changed
            then solveRcEligibilityFixpoint(comps)(newEligible)
            else newEligible

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
        let adj = getNodeAdj(nodes)
        in
            let facts = getNodeFacts(nodes)
            in
                let unambiguousMap = getNodeUnambiguous(nodes)
                in
                    let postOrder = computePostOrder(names)(adj)([])([])
                    in
                        let sccs = computeSccs(postOrder)(adj)([])([])
                        in
                            let comps = buildComponentsAux(sccs)(sccs)(adj)(facts)(0)
                            in
                                let eligibleComps = solveRcEligibilityFixpoint(comps)([])
                                in resolveNodeProvenances(names)(sccs)(eligibleComps)(facts)(unambiguousMap))
