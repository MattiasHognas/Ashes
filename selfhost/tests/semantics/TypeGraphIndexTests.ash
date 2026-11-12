import Ashes.Test as test
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.TypeGraphIndex
import AshesCompiler.Semantics.HeapLayoutClassification.heapNamedTypeConstructors as referenceConstructors
export (
    value runTypeGraphIndexTests,
    value runTypeGraphPointerIndexTests,
    value runTypeGraphCollisionTests,
    value runFreshTypeGraphIndexTest,
)

let recursive constructorBody fields result =
    match fields with
        | [] -> result
        | field :: rest ->
            SemFunction(field)(constructorBody(rest)(result))(None)

let constructor name result fields = ConstructorInferenceDefinition(name = name, scheme = TypeScheme(quantified = [], body = constructorBody(fields)(result), constraints = []), fieldNames = [])

let named id name arguments = SemNamed(id)(name)(arguments)

let testEnvironment suffix =
    (let box = named(0)("Box" + suffix)([SemVariable(71)])
    in
        let growing = named(1)("Growing" + suffix)([SemVariable(71)])
        in
            let environment = emptyTypeEnvironment(Unit)
            in
                let context = addExternalTypeDefinition("Handle" + suffix)(Some("close"))(environment.typeResolutionContext)
                in
                    typeGraphExtendConstructorEnvironment((environment with typeResolutionContext = context))([
                        constructor("Empty" + suffix)(box)([]),
                        constructor("Box" + suffix)(box)([SemVariable(71), SemInt]),
                        constructor("Other" + suffix)(named(0)("Other" + suffix)([]))([SemString]),
                        constructor("OtherBox" + suffix)(named(9)("Box" + suffix)([]))([SemBytes]),
                        constructor("Phantom" + suffix)(named(2)("Phantom" + suffix)([SemVariable(71)]))([]),
                        constructor("Grow" + suffix)(growing)([named(1)("Growing" + suffix)([SemList(SemVariable(71))])])
                    ]))

let recursive checkReference types index =
    match types with
        | [] -> index
        | semanticType :: rest ->
            match typeGraphNode(semanticType)(index) with
                | (node, next) ->
                    node.constructorGroups
                    |> test.assertEqual(referenceConstructors(node.resolvedType)(index.environment))
                    |> (given (_) -> checkReference(rest)(next))

let testInstantiatedStructure unit =
    (let environment = testEnvironment("")
    in
        let queries = [named(0)("Box")([SemInt]), named(0)("Box")([SemString]), named(0)("Other")([]), named(9)("Box")([]), SemList(SemString), SemTuple([SemString, SemBytes])]
        in
            let initial = emptyTypeGraphIndex(environment)
            in
                let indexed = checkReference(queries)(initial)
                in
                    let repeated = checkReference(queries)(indexed)
                    in
                        indexed.nodeCount
                        |> test.assertEqual(6)
                        |> (given (_) -> test.assertEqual(6)(repeated.nodeCount))
                        |> (given (_) -> test.assertEqual(0)(initial.nodeCount))
                        |> (given (_) ->
                            match typeGraphNode(named(0)("Box")([SemString]))(repeated) with
                                | (node, _next) ->
                                    node.constructorGroups
                                    |> test.assertEqual([("Empty", []), ("Box", [SemString, SemInt])])
                                    |> (given (_) -> test.assertEqual([SemString, SemInt])(node.resourceChildren))
                                    |> (given (_) -> test.assertEqual([SemString, SemString, SemInt])(node.unresolvedChildren))))

let testEdgePolicies unit =
    (let index =
        ""
        |> testEnvironment
        |> emptyTypeGraphIndex
    in
        match typeGraphNode(named(2)("Phantom")([SemVariable(92)]))(index) with
            | (phantom, next) ->
                phantom.resourceChildren
                |> test.assertEqual([])
                |> (given (_) -> test.assertEqual([SemVariable(92)])(phantom.unresolvedChildren))
                |> (given (_) ->
                    match typeGraphNode(SemOpaque("Handle"))(next) with
                        | (handleNode, afterHandle) ->
                            handleNode.directResource
                            |> test.assertEqual(true)
                            |> (given (_) ->
                                match typeGraphNode(SemNamed(88)("Socket")([]))(afterHandle) with
                                    | (socket, _last) -> test.assertEqual(true)(socket.directResource))))

// Runtime resolution preserves pointer wrappers. Pointer components participate in argument
// containment, while a pointer is not a resource-containment edge.
let runTypeGraphPointerIndexTests unit =
    match ""
    |> testEnvironment
    |> emptyTypeGraphIndex
    |> typeGraphNode(SemPointer(SemOpaque("Handle"))) with
        | (pointer, _index) ->
            pointer.resolvedType
            |> test.assertEqual(SemPointer(SemOpaque("Handle")))
            |> (given (_) -> test.assertEqual([])(pointer.resourceChildren))
            |> (given (_) -> test.assertEqual([SemOpaque("Handle")])(pointer.argumentComponents))
            |> (given (_) -> test.assertEqual(false)(pointer.directResource))

let testLazyNonregularGraph unit =
    match ""
    |> testEnvironment
    |> emptyTypeGraphIndex
    |> typeGraphNode(named(1)("Growing")([SemInt])) with
        | (node, index) ->
            node.fields
            |> test.assertEqual([named(1)("Growing")([SemList(SemInt)])])
            |> (given (_) -> test.assertEqual(1)(index.nodeCount))
            |> (given (_) ->
                match typeGraphNode(named(1)("Growing")([SemList(SemInt)]))(index) with
                    | (child, next) ->
                        child.fields
                        |> test.assertEqual([named(1)("Growing")([SemList(SemList(SemInt))])])
                        |> (given (_) -> test.assertEqual(2)(next.nodeCount)))

let testFactSeparation unit =
    (let semanticType = named(0)("Box")([SemVariable(92)])
    in
        let initial =
            ""
            |> testEnvironment
            |> emptyTypeGraphIndex
        in
            let indexed = completeTypeGraphRootFact(ResourceFact)(semanticType)(false)(initial)
            in
                let completed = completeTypeGraphRootFact(UnresolvedFact)(semanticType)(true)(indexed)
                in
                    initial
                    |> typeGraphRootFact(ResourceFact)(semanticType)
                    |> test.assertEqual(None)
                    |> (given (_) ->
                        completed
                        |> typeGraphRootFact(ResourceFact)(semanticType)
                        |> test.assertEqual(Some(false)))
                    |> (given (_) ->
                        completed
                        |> typeGraphRootFact(UnresolvedFact)(semanticType)
                        |> test.assertEqual(Some(true)))
                    |> (given (_) ->
                        completed
                        |> typeGraphRootFact(ResourceFact)(named(0)("Box")([SemString]))
                        |> test.assertEqual(None))
                    |> (given (_) ->
                        indexed
                        |> typeGraphRootFact(UnresolvedFact)(semanticType)
                        |> test.assertEqual(None))
                    |> (given (_) -> test.assertEqual(1)(completed.nodeCount)))

let testZeroCostAndEnvironmentScope unit =
    (let environment =
        typeGraphExtendConstructorEnvironment(testEnvironment(""))([ConstructorInferenceDefinition(name = "Wrap", scheme = TypeScheme(quantified = [], body = SemFunction(SemVariable(71))(named(31)("Wrapper")([SemVariable(71)]))(None), constraints = []), fieldNames = ["value"])])
    in
        let context =
            addZeroCostTypeDefinitionWithProvenance(31)("Wrapper")([71])(SemParameter(71)("a"))(DeclarationProvenance(packageId = "test"))(environment.typeResolutionContext)
        in
            match typeGraphNode(named(31)("Wrapper")([SemOpaque("Handle")]))(emptyTypeGraphIndex((environment with typeResolutionContext = context))) with
                | (wrapped, _index) ->
                    wrapped.resolvedType
                    |> test.assertEqual(SemOpaque("Handle"))
                    |> (given (_) -> test.assertEqual(true)(wrapped.directResource))
                    |> (given (_) -> test.assertEqual(true)(wrapped.sourceIsRecord))
                    |> (given (_) -> test.assertEqual(false)(wrapped.isRecord))
                    |> (given (_) -> test.assertEqual([])(wrapped.constructorGroups))
                    |> (given (_) -> test.assertEqual([("Wrap", [SemOpaque("Handle")])])(wrapped.sourceConstructorGroups))
                    |> (given (_) ->
                        match Unit
                        |> emptyTypeEnvironment
                        |> emptyTypeGraphIndex
                        |> typeGraphNode(SemOpaque("Handle")) with
                            | (unregistered, _fresh) -> test.assertEqual(false)(unregistered.directResource)))

let testDuplicateGroupsKeepFirst unit =
    (let environment = testEnvironment("")
    in
        let extended =
            typeGraphExtendConstructorEnvironment(environment)([constructor("Later")(named(0)("Other")([]))([SemInt])])
        in
            match extended
            |> emptyTypeGraphIndex
            |> typeGraphNode(named(0)("Other")([])) with
                | (node, _index) -> test.assertEqual([("Other", [SemString])])(node.constructorGroups))

// Each call creates dynamic names, an environment and several exact instantiations, then releases
// every graph root. Native lifetime harnesses repeatedly call this function without retaining an
// index between iterations; repeated-hit checks alone would miss growth from whole dead caches.
let runFreshTypeGraphIndexTest iteration =
    (let suffix = Ashes.Text.fromInt(iteration)
    in
        let semanticType = named(0)("Box" + suffix)([SemOpaque("Handle" + suffix)])
        in
            match suffix
            |> testEnvironment
            |> emptyTypeGraphIndex
            |> typeGraphNode(semanticType) with
                | (node, index) ->
                    let completed = completeTypeGraphRootFact(ResourceFact)(semanticType)(true)(index)
                    in
                        match typeGraphNode(semanticType)(completed) with
                            | (repeated, finalIndex) ->
                                repeated.fields
                                |> test.assertEqual(node.fields)
                                |> (given (_) -> test.assertEqual(1)(finalIndex.nodeCount))
                                |> (given (_) ->
                                    finalIndex
                                    |> typeGraphRootFact(ResourceFact)(semanticType)
                                    |> test.assertEqual(Some(true))))

let comparatorTypes unit =
    [
        SemInt,
        SemUInt(8),
        SemUInt(64),
        SemFloat,
        SemBigInt,
        SemString,
        SemRune,
        SemBytes,
        SemBool,
        SemNever,
        SemList(SemInt),
        SemList(SemString),
        SemTuple([]),
        SemTuple([SemInt, SemString]),
        SemTuple([SemString, SemInt]),
        SemFunction(SemInt)(SemInt)(None),
        SemFunction(SemString)(SemInt)(None),
        SemFunction(SemInt)(SemString)(None),
        None
        |> SemRow([])
        |> Some
        |> SemFunction(SemInt)(SemInt),
        SemVariable(-1),
        SemVariable(0),
        SemCapability("A")([SemInt]),
        SemCapability("B")([SemInt]),
        SemCapability("A")([SemString]),
        SemRow([SemInt, SemString])(None),
        SemRow([SemString, SemInt])(None),
        SemRow([SemInt, SemString])(Some(SemVariable(0))),
        SemNamed(0)("A")([SemInt]),
        SemNamed(1)("A")([SemInt]),
        SemNamed(0)("B")([SemInt]),
        SemNamed(0)("A")([SemString]),
        SemParameter(0)("a"),
        SemParameter(1)("a"),
        SemParameter(0)("b"),
        SemOpaque("A"),
        SemOpaque("B"),
        SemPointer(SemInt),
        SemPointer(SemString)
    ]

let recursive checkComparatorRow left rights =
    match rights with
        | [] -> Unit
        | right :: rest ->
            let order = compareTypeGraphKeys(left)(right)
            in
                order == 0
                |> test.assertEqual(left == right)
                |> (given (_) -> test.assertEqual(0)(order + compareTypeGraphKeys(right)(left)))
                |> (given (_) -> checkComparatorRow(left)(rest))

let recursive checkComparatorTypes remaining all =
    match remaining with
        | [] -> Unit
        | left :: rest ->
            all
            |> checkComparatorRow(left)
            |> (given (_) -> checkComparatorTypes(rest)(all))

let testCompleteKeyComparator unit =
    (let types = comparatorTypes(Unit)
    in checkComparatorTypes(types)(types))

let runTypeGraphIndexTests unit =
    Unit
    |> testCompleteKeyComparator
    |> testInstantiatedStructure
    |> testEdgePolicies
    |> testLazyNonregularGraph
    |> testFactSeparation
    |> testZeroCostAndEnvironmentScope
    |> testDuplicateGroupsKeepFirst
    |> (given (_) -> runFreshTypeGraphIndexTest(17))

// Distinct IDs separated by the fingerprint modulus collide through nested type structure too.
// Every collision must preserve exact node identity, completed facts and immutable snapshots.
let collisionTypePairs unit =
    (let first = SemVariable(0)
    in
        let second = SemVariable(2147483647)
        in
            [
                (first, second),
                (SemNamed(0)("Collision")([SemInt]), SemNamed(2147483647)("Collision")([SemInt])),
                (SemParameter(0)("a"), SemParameter(2147483647)("a")),
                (SemList(first), SemList(second)),
                (SemTuple([SemInt, first]), SemTuple([SemInt, second])),
                (SemFunction(SemInt)(first)(None), SemFunction(SemInt)(second)(None)),
                (SemRow([SemString])(Some(first)), SemRow([SemString])(Some(second))),
                (SemPointer(first), SemPointer(second))
            ])

let lookupNodeId semanticType index =
    match typeGraphLookupNode(semanticType)(index) with
        | Some(node) -> Some(node.nodeId)
        | None -> None

let recursive checkCollisionPairs pairs index =
    match pairs with
        | [] -> index
        | (first, second) :: rest ->
            first
            |> typeGraphFingerprint
            |> test.assertEqual(typeGraphFingerprint(second))
            |> (given (_) ->
                match typeGraphNode(first)(index) with
                    | (firstNode, afterFirst) ->
                        index
                        |> lookupNodeId(first)
                        |> test.assertEqual(None)
                        |> (given (_) ->
                            afterFirst
                            |> lookupNodeId(first)
                            |> test.assertEqual(Some(firstNode.nodeId)))
                        |> (given (_) ->
                            afterFirst
                            |> lookupNodeId(second)
                            |> test.assertEqual(None))
                        |> (given (_) ->
                            match typeGraphNode(second)(afterFirst) with
                                | (secondNode, afterSecond) ->
                                    let completed = completeTypeGraphRootFact(ResourceFact)(first)(false)(afterSecond)
                                    in
                                        match typeGraphNode(first)(completed) with
                                            | (repeated, afterRepeat) ->
                                                firstNode.nodeId != secondNode.nodeId
                                                |> test.assertEqual(true)
                                                |> (given (_) -> test.assertEqual(first)(repeated.sourceType))
                                                |> (given (_) -> test.assertEqual(second)(secondNode.sourceType))
                                                |> (given (_) -> test.assertEqual(firstNode.nodeId)(repeated.nodeId))
                                                |> (given (_) ->
                                                    afterRepeat
                                                    |> lookupNodeId(first)
                                                    |> test.assertEqual(Some(firstNode.nodeId)))
                                                |> (given (_) ->
                                                    afterRepeat
                                                    |> lookupNodeId(second)
                                                    |> test.assertEqual(Some(secondNode.nodeId)))
                                                |> (given (_) ->
                                                    afterRepeat
                                                    |> typeGraphRootFact(ResourceFact)(first)
                                                    |> test.assertEqual(Some(false)))
                                                |> (given (_) ->
                                                    afterRepeat
                                                    |> typeGraphRootFact(ResourceFact)(second)
                                                    |> test.assertEqual(None))
                                                |> (given (_) ->
                                                    afterSecond
                                                    |> typeGraphRootFact(ResourceFact)(first)
                                                    |> test.assertEqual(None))
                                                |> (given (_) -> checkCollisionPairs(rest)(afterRepeat))))

let runTypeGraphCollisionTests unit =
    (let result =
        Unit
        |> emptyTypeEnvironment
        |> emptyTypeGraphIndex
        |> checkCollisionPairs(collisionTypePairs(Unit))
    in test.assertEqual(16)(result.nodeCount))
