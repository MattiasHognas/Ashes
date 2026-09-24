// Whole-program result provenance and forwarding SCC analysis.
//
// Invariants:
// - Direct constructor applications, tuples, list/record literals, string additions, and fresh
//   producers are RC-eligible.
// - Forwarding calls inherit their callee's provenance.
// - Strongly-connected components are solved as a least fixpoint: productive recursion with a fresh
//   base converges, while pure forwarding cycles and non-RC paths fail closed.
//
// Names are looked up in balanced maps (Ashes.Collection.Map) so a program with thousands of
// functions is analysed in near-linear time; every map keeps the FIRST node of a name, as the
// association lists it replaces answered their first match, and the traversal orders (node order,
// forward-target order, Kosaraju's post order) are the ones stage 0 walks.

import AshesCompiler.Semantics.OwnershipSummary
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
export (
    type ProvenanceFunctionNode(..),
    type ProvenanceComponent(..),
    value buildProvenanceNode,
    value computeStronglyConnectedComponents,
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

// The per-function facts a component aggregates over its members.
type NodeFacts =
    | factDirect: Bool
    | factRejected: Bool
    | factArms: Int
    | factBytes: List(BytesOwnershipProvenance)
    | factUnknownBytes: Bool

type alias NameSet = MapTree(Str, Bool)

type alias Adjacency = MapTree(Str, List(Str))

let emptyFacts = NodeFacts(factDirect = false, factRejected = false, factArms = 0, factBytes = [], factUnknownBytes = false)

let containsName (name: Str) (names: NameSet) =
    match Ashes.Collection.Map.getStr(name)(names) with
        | Some(_present) -> true
        | None -> false

let addName (name: Str) (names: NameSet) = Ashes.Collection.Map.setStr(name)(true)(names)

let targetsOf (adj: Adjacency) (name: Str) =
    match Ashes.Collection.Map.getStr(name)(adj) with
        | Some(targets) -> targets
        | None -> []

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

// The list builders below accumulate and reverse once rather than cons around a recursive call:
// stage 0 copies the partial list out at every return of a builder whose element is a string or
// list borrowed from a record, which made each of them quadratic in memory.
let recursive getNodeNamesInto (nodes: List(ProvenanceFunctionNode)) (names: List(Str)) =
    match nodes with
        | [] -> reverse(names)
        | ProvenanceFunctionNode { functionName = name } :: tail -> getNodeNamesInto(tail)(name :: names)

let getNodeNames (nodes: List(ProvenanceFunctionNode)) = getNodeNamesInto(nodes)([])

let recursive adjacencyOf (nodes: List(ProvenanceFunctionNode)) (adj: Adjacency) =
    match nodes with
        | [] -> adj
        | ProvenanceFunctionNode { functionName = name, forwardTargets = targets } :: tail ->
            adj
            |> Ashes.Collection.Map.upsertStr(name)(targets)(given (existing) -> existing)
            |> adjacencyOf(tail)

let recursive factsOf (nodes: List(ProvenanceFunctionNode)) (facts: MapTree(Str, NodeFacts)) =
    match nodes with
        | [] -> facts
        | ProvenanceFunctionNode { functionName = name, hasDirectEligibleResult = d, hasRejectedResult = r, consideredArmCount = a, directBytesProvenances = b, hasUnknownBytesResult = u } :: tail ->
            facts
            |> Ashes.Collection.Map.upsertStr(name)(NodeFacts(factDirect = d, factRejected = r, factArms = a, factBytes = b, factUnknownBytes = u))(given (existing) -> existing)
            |> factsOf(tail)

let recursive unambiguousOf (nodes: List(ProvenanceFunctionNode)) (targets: MapTree(Str, Maybe(Str))) =
    match nodes with
        | [] -> targets
        | ProvenanceFunctionNode { functionName = name, unambiguousForwardTarget = unambiguous } :: tail ->
            targets
            |> Ashes.Collection.Map.upsertStr(name)(unambiguous)(given (existing) -> existing)
            |> unambiguousOf(tail)

// The reverse adjacency Kosaraju's second pass walks: every function naming `target` among its
// forward targets, in node order and once per node. Built once over the nodes in reverse, so
// consing yields node order, rather than by scanning every node at each visit.
let recursive addPredecessor (name: Str) (targets: List(Str)) (added: NameSet) (predecessors: Adjacency) =
    match targets with
        | [] -> predecessors
        | target :: rest ->
            if containsName(target)(added)
            then addPredecessor(name)(rest)(added)(predecessors)
            else
                predecessors
                |> Ashes.Collection.Map.upsertStr(target)([name])(given (existing) -> name :: existing)
                |> addPredecessor(name)(rest)(Ashes.Collection.Map.setStr(target)(true)(added))

let recursive predecessorsOf (reversedNodes: List(ProvenanceFunctionNode)) (predecessors: Adjacency) =
    match reversedNodes with
        | [] -> predecessors
        | ProvenanceFunctionNode { functionName = name, forwardTargets = targets } :: tail ->
            predecessors
            |> addPredecessor(name)(targets)(Ashes.Collection.Map.empty)
            |> predecessorsOf(tail)

// --- Graph operations & SCC via Kosaraju's algorithm ---
// Both passes walk with an explicit stack in one loop, so a visit neither recurses nor hands a
// `(visited, ...)` pair back per node: a pair returned out of a call clones the visited map it
// carries, which made the walk quadratic in memory. The stack holds whole worklists rather than
// one name per frame, so pushing a node's targets is one cons and never rebuilds the stack (a
// helper returning a list that extends its borrowed argument copies the whole list out). `Visit`
// carries the names still to enter at one level and `Exit` records a name once every target of
// its level is done, so the post order is exactly the recursive walk's; the reverse walk records a
// name on entry, so its stack needs no exit frames.
type DfsFrame =
    | Visit(List(Str))
    | Exit(Str)

// The post order of the whole graph: every root in `roots` is walked in turn, a name an earlier
// root's walk already visited skipped.
let recursive computePostOrder (roots: List(Str)) (stack: List(DfsFrame)) (adj: Adjacency) (visited: NameSet) (postOrder: List(Str)) =
    match stack with
        | Visit([]) :: rest -> computePostOrder(roots)(rest)(adj)(visited)(postOrder)
        | Visit(name :: more) :: rest ->
            if containsName(name)(visited)
            then computePostOrder(roots)(Visit(more) :: rest)(adj)(visited)(postOrder)
            else
                computePostOrder(roots)(Visit(targetsOf(adj)(name)) :: Exit(name) :: Visit(more) :: rest)(adj)(Ashes.Collection.Map.setStr(name)(true)(visited))(postOrder)
        | Exit(name) :: rest -> computePostOrder(roots)(rest)(adj)(visited)(name :: postOrder)
        | [] ->
            match roots with
                | [] -> postOrder
                | root :: rest -> computePostOrder(rest)([Visit([root])])(adj)(visited)(postOrder)

// The components in the order the post order yields them, each member list in the order the
// reverse walk records it, most recently visited first.
let recursive computeSccs (order: List(Str)) (stack: List(List(Str))) (predecessors: Adjacency) (visited: NameSet) (component: List(Str)) (components: List(List(Str))) =
    match stack with
        | [] :: rest -> computeSccs(order)(rest)(predecessors)(visited)(component)(components)
        | (name :: more) :: rest ->
            if containsName(name)(visited)
            then computeSccs(order)(more :: rest)(predecessors)(visited)(component)(components)
            else
                computeSccs(order)(targetsOf(predecessors)(name) :: more :: rest)(predecessors)(Ashes.Collection.Map.setStr(name)(true)(visited))(name :: component)(components)
        | [] ->
            let finished =
                match component with
                    | [] -> components
                    | _ -> component :: components
            in
                match order with
                    | [] -> finished
                    | head :: tail ->
                        if containsName(head)(visited)
                        then computeSccs(tail)([])(predecessors)(visited)([])(finished)
                        else computeSccs(tail)([[head]])(predecessors)(visited)([])(finished)

let sccsOf (nodes: List(ProvenanceFunctionNode)) (adj: Adjacency) =
    computeSccs(computePostOrder(getNodeNames(nodes))([])(adj)(Ashes.Collection.Map.empty)([]))([])(predecessorsOf(reverse(nodes))(Ashes.Collection.Map.empty))(Ashes.Collection.Map.empty)([])([])

let computeStronglyConnectedComponents (nodes: List(ProvenanceFunctionNode)) =
    Ashes.Collection.Map.empty
    |> adjacencyOf(nodes)
    |> sccsOf(nodes)

// --- Component building and fixpoint solver ---
// Each function's component id, the first component listing it winning as the positional search
// it replaces did.
let recursive addMemberIds (members: List(Str)) (currentId: Int) (ids: MapTree(Str, Int)) =
    match members with
        | [] -> ids
        | member :: rest ->
            ids
            |> Ashes.Collection.Map.upsertStr(member)(currentId)(given (existing) -> existing)
            |> addMemberIds(rest)(currentId)

let recursive componentIdsOf (components: List(List(Str))) (currentId: Int) (ids: MapTree(Str, Int)) =
    match components with
        | [] -> ids
        | members :: tail ->
            ids
            |> addMemberIds(members)(currentId)
            |> componentIdsOf(tail)(currentId + 1)

let componentIdOf (ids: MapTree(Str, Int)) (func: Str) =
    match Ashes.Collection.Map.getStr(func)(ids) with
        | Some(id) -> id
        | None -> -1

let recursive collectDependenciesFromTargets (targets: List(Str)) (ids: MapTree(Str, Int)) (selfId: Int) =
    match targets with
        | [] -> []
        | target :: rest ->
            let targetCompId = componentIdOf(ids)(target)
            in
                let restDeps = collectDependenciesFromTargets(rest)(ids)(selfId)
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

let recursive collectComponentDependencies (members: List(Str)) (adj: Adjacency) (ids: MapTree(Str, Int)) (selfId: Int) =
    match members with
        | [] -> []
        | func :: rest ->
            let deps =
                collectDependenciesFromTargets(targetsOf(adj)(func))(ids)(selfId)
            in
                let restDeps = collectComponentDependencies(rest)(adj)(ids)(selfId)
                in unionIntLists(deps)(restDeps)

let factOf (facts: MapTree(Str, NodeFacts)) (func: Str) =
    match Ashes.Collection.Map.getStr(func)(facts) with
        | Some(fact) -> fact
        | None -> emptyFacts

let recursive aggregateComponentFacts (members: List(Str)) (facts: MapTree(Str, NodeFacts)) =
    match members with
        | [] -> emptyFacts
        | func :: rest ->
            match (factOf(facts)(func), aggregateComponentFacts(rest)(facts)) with
                | (first, others) ->
                    NodeFacts(
                        factDirect = first.factDirect || others.factDirect,
                        factRejected = first.factRejected || others.factRejected,
                        factArms = first.factArms + others.factArms,
                        factBytes = unionBytesProvenances(first.factBytes)(others.factBytes),
                        factUnknownBytes = first.factUnknownBytes || others.factUnknownBytes
                    )

let recursive buildComponentsFrom (sccs: List(List(Str))) (adj: Adjacency) (ids: MapTree(Str, Int)) (facts: MapTree(Str, NodeFacts)) (currentId: Int) =
    match sccs with
        | [] -> []
        | members :: tail ->
            match aggregateComponentFacts(members)(facts) with
                | NodeFacts { factDirect = direct, factRejected = rejected, factArms = arms, factBytes = bProv, factUnknownBytes = unkBytes } ->
                    let deps = collectComponentDependencies(members)(adj)(ids)(currentId)
                    in
                        let comp =
                            ProvenanceComponent(
                                componentId = currentId,
                                memberFunctions = members,
                                hasDirectEligibleResult = direct,
                                hasRejectedResult = rejected || arms == 0,
                                consideredArmCount = arms,
                                dependencies = deps,
                                directBytesProvenances = bProv,
                                hasUnknownBytesResult = unkBytes
                            )
                        in comp :: buildComponentsFrom(tail)(adj)(ids)(facts)(currentId + 1)

let buildComponents (sccs: List(List(Str))) (adj: Adjacency) (ids: MapTree(Str, Int)) (facts: MapTree(Str, NodeFacts)) = buildComponentsFrom(sccs)(adj)(ids)(facts)(0)

let isEligible (compId: Int) (eligible: MapTree(Int, Bool)) =
    match Ashes.Collection.Map.getInt(compId)(eligible) with
        | Some(_present) -> true
        | None -> false

let recursive allDependenciesEligible (deps: List(Int)) (eligible: MapTree(Int, Bool)) =
    match deps with
        | [] -> true
        | depId :: rest ->
            if isEligible(depId)(eligible)
            then allDependenciesEligible(rest)(eligible)
            else false

let recursive anyDependencyEligible (deps: List(Int)) (eligible: MapTree(Int, Bool)) =
    match deps with
        | [] -> false
        | depId :: rest ->
            if isEligible(depId)(eligible)
            then true
            else anyDependencyEligible(rest)(eligible)

let recursive solveRcEligibilityStep (comps: List(ProvenanceComponent)) (eligible: MapTree(Int, Bool)) (changed: Bool) =
    match comps with
        | [] -> (eligible, changed)
        | ProvenanceComponent { componentId = compId, hasDirectEligibleResult = hasDirect, hasRejectedResult = hasRejected, dependencies = deps } :: rest ->
            if isEligible(compId)(eligible) || hasRejected
            then solveRcEligibilityStep(rest)(eligible)(changed)
            else
                if (hasDirect || anyDependencyEligible(deps)(eligible)) && allDependenciesEligible(deps)(eligible)
                then
                    solveRcEligibilityStep(rest)(Ashes.Collection.Map.setInt(compId)(true)(eligible))(true)
                else solveRcEligibilityStep(rest)(eligible)(changed)

let recursive solveRcEligibilityFixpoint (comps: List(ProvenanceComponent)) (eligible: MapTree(Int, Bool)) =
    match solveRcEligibilityStep(comps)(eligible)(false) with
        | (newEligible, changed) ->
            if changed
            then solveRcEligibilityFixpoint(comps)(newEligible)
            else newEligible

let recursive resolveNodeProvenancesInto (names: List(Str)) (ids: MapTree(Str, Int)) (eligible: MapTree(Int, Bool)) (facts: MapTree(Str, NodeFacts)) (unambiguousMap: MapTree(Str, Maybe(Str))) (resolved: List((Str, FunctionResultProvenance))) =
    match names with
        | [] -> reverse(resolved)
        | name :: tail ->
            let unambiguous =
                match Ashes.Collection.Map.getStr(name)(unambiguousMap) with
                    | Some(target) -> target
                    | None -> None
            in
                let bProv =
                    match factOf(facts)(name) with
                        | NodeFacts { factBytes = single :: [] } -> single
                        | _ -> BytesProvenanceUnknown
                in
                    let prov =
                        FunctionResultProvenance(
                            rcEligible = isEligible(componentIdOf(ids)(name))(eligible),
                            forwardsTo = unambiguous,
                            bytesProvenance = bProv
                        )
                    in resolveNodeProvenancesInto(tail)(ids)(eligible)(facts)(unambiguousMap)((name, prov) :: resolved)

let resolveNodeProvenances (names: List(Str)) (ids: MapTree(Str, Int)) (eligible: MapTree(Int, Bool)) (facts: MapTree(Str, NodeFacts)) (unambiguousMap: MapTree(Str, Maybe(Str))) = resolveNodeProvenancesInto(names)(ids)(eligible)(facts)(unambiguousMap)([])

let resolveResultProvenances (nodes: List(ProvenanceFunctionNode)) =
    (let adj = adjacencyOf(nodes)(Ashes.Collection.Map.empty)
    in
        let facts = factsOf(nodes)(Ashes.Collection.Map.empty)
        in
            let sccs = sccsOf(nodes)(adj)
            in
                let ids = componentIdsOf(sccs)(0)(Ashes.Collection.Map.empty)
                in
                    let comps = buildComponents(sccs)(adj)(ids)(facts)
                    in
                        let eligibleComps = solveRcEligibilityFixpoint(comps)(Ashes.Collection.Map.empty)
                        in
                            Ashes.Collection.Map.empty
                            |> unambiguousOf(nodes)
                            |> resolveNodeProvenances(getNodeNames(nodes))(ids)(eligibleComps)(facts))
