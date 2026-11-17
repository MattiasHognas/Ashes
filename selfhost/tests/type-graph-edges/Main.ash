import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.TypeGraphIndex
import AshesCompiler.Semantics.TypeGraphQueries
let assert label condition =
    if condition
    then Unit
    else Ashes.IO.panic(label)

let edge kind parent position index =
    (let table =
        match kind with
            | ResourceFact -> index.resourceEdges
            | UnresolvedFact -> index.unresolvedEdges
    in
        match Ashes.Collection.Map.getInt(parent)(table) with
            | None -> None
            | Some(children) -> Ashes.Collection.Map.getInt(position)(children))

let edgeId kind parent position index =
    match edge(kind)(parent)(position)(index) with
        | None -> None
        | Some(node) -> Some(node.nodeId)

let retainEdge kind parent position semanticType index =
    match typeGraphResolvedChild(kind)(parent)(position)(semanticType)(index) with
        | (_node, next) -> next

let growing argument = SemNamed(31)("Growing")([argument])

let growingEnvironment unit = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 31, groupTypeName = "Growing", groupIsGeneric = true, groupFields = [], groupGenericFields = [("Grow", [growing(SemList(SemVariable(71)))], [SemVariable(71)])])]

let checkLazy unit =
    (let initial =
        Unit
        |> growingEnvironment
        |> emptyTypeGraphIndex
    in
        match typeGraphResource(growing(SemInt))(initial) with
            | (answer, next) ->
                !answer && next.nodeCount == 2 && initial.nodeCount == 0
                |> assert("edge assertion line 39")
                |> (given (_) -> assert("edge assertion line 40")(edgeId(ResourceFact)(0)(0)(next) == Some(1)))
                |> (given (_) -> assert("edge assertion line 41")(edgeId(ResourceFact)(1)(0)(next) == None))
                |> (given (_) ->
                    match typeGraphDroppable(growing(SemInt))(next) with
                        // The drop walk queries the child's resource closure before
                        // cutting its path, which discovers one further instantiation.
                        | (droppable, final) -> assert("nonregular drop discovery: " + Ashes.Text.fromInt(final.nodeCount))(droppable && final.nodeCount == 3)))

let checkShortCircuit unit =
    (let initial =
        Unit
        |> growingEnvironment
        |> emptyTypeGraphIndex
    in
        let root = SemTuple([SemNamed(99)("FileHandle")([]), growing(SemInt)])
        in
            match typeGraphResource(root)(initial) with
                | (answer, next) ->
                    answer && next.nodeCount == 2
                    |> assert("edge assertion line 57")
                    |> (given (_) -> assert("cold edge is not retained")(edgeId(ResourceFact)(0)(0)(next) == None))
                    |> (given (_) ->
                        assert("revisited edge is retained")(edgeId(ResourceFact)(0)(0)(retainEdge(ResourceFact)(0)(0)(SemNamed(99)("FileHandle")([]))(next)) == Some(1)))
                    |> (given (_) -> assert("edge assertion line 59")(edgeId(ResourceFact)(0)(1)(next) == None))
                    |> (given (_) ->
                        match typeGraphLookupNode(growing(SemInt))(next) with
                            | None -> Unit
                            | Some(_) -> Ashes.IO.panic("eager sibling")))

let checkPolicies unit =
    (let root = SemNamed(21)("Phantom")([SemVariable(91)])
    in
        let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 21, groupTypeName = "Phantom", groupIsGeneric = false, groupFields = [("Phantom", [SemString])], groupGenericFields = [])]
        in
            match environment
            |> emptyTypeGraphIndex
            |> typeGraphResource(root) with
                | (resource, resourceIndex) ->
                    match typeGraphUnresolved(root)(resourceIndex) with
                        | (unresolved, final) ->
                            !resource && unresolved
                            |> assert("edge assertion line 77")
                            |> (given (_) -> assert("edge assertion line 78")(edgeId(UnresolvedFact)(0)(0)(resourceIndex) == None))
                            |> (given (_) ->
                                let warmed =
                                    final
                                    |> retainEdge(ResourceFact)(0)(0)(SemString)
                                    |> retainEdge(UnresolvedFact)(0)(0)(SemVariable(91))
                                in
                                    match (edge(ResourceFact)(0)(0)(warmed), edge(UnresolvedFact)(0)(0)(warmed)) with
                                        | (Some(first), Some(second)) -> assert("edge assertion line 81")(first.resolvedType == SemString && second.resolvedType == SemVariable(91) && first.nodeId != second.nodeId)
                                        | _ -> Ashes.IO.panic("missing policy edge")))

let checkCollisions unit =
    (let first = SemVariable(0)
    in
        let second = SemVariable(2147483647)
        in
            let root = SemTuple([first, second, first])
            in
                let initial =
                    Unit
                    |> emptyTypeEnvironment
                    |> emptyTypeGraphIndex
                in
                    match typeGraphResource(root)(initial) with
                        | (resource, next) ->
                            !resource && typeGraphFingerprint(first) == typeGraphFingerprint(second) && next.nodeCount == 3
                            |> assert("edge assertion line 99")
                            |> (given (_) -> assert("first cold edge omitted")(edgeId(ResourceFact)(0)(0)(next) == None))
                            |> (given (_) -> assert("duplicate edge retained")(edgeId(ResourceFact)(0)(2)(next) == Some(1)))
                            |> (given (_) ->
                                let warmed =
                                    next
                                    |> retainEdge(ResourceFact)(0)(0)(first)
                                    |> retainEdge(ResourceFact)(0)(1)(second)
                                in assert("collision identity preserved")(edgeId(ResourceFact)(0)(0)(warmed) == edgeId(ResourceFact)(0)(2)(warmed) && edgeId(ResourceFact)(0)(0)(warmed) != edgeId(ResourceFact)(0)(1)(warmed)))
                            |> (given (_) -> assert("edge assertion line 102")(edgeId(ResourceFact)(0)(0)(initial) == None))
                            |> (given (_) ->
                                match typeGraphDroppable(root)(next) with
                                    | (droppable, final) -> assert("edge assertion line 105")(!droppable && final.nodeCount == 3)))

let checkAlias unit =
    (let alias = SemNamed(42)("Alias")([])
    in
        let root = SemTuple([alias, alias])
        in
            let environment =
                Unit
                |> emptyTypeEnvironment
                |> addInferenceZeroCostTypeDefinition(42)("Alias")([])(SemString)
            in
                match environment
                |> emptyTypeGraphIndex
                |> typeGraphDroppable(root) with
                    | (answer, next) ->
                        answer && next.nodeCount == 3
                        |> assert("edge assertion line 122")
                        |> (given (_) ->
                            assert("alias edge sharing")(edgeId(ResourceFact)(0)(0)(retainEdge(ResourceFact)(0)(0)(alias)(next)) == edgeId(ResourceFact)(0)(1)(next)))
                        |> (given (_) ->
                            match edge(ResourceFact)(0)(1)(next) with
                                | Some(node) -> assert("edge assertion line 126")(node.resolvedType == SemString && node.sourceMatchesResolved)
                                | None -> Ashes.IO.panic("missing alias edge"))
                        |> (given (_) ->
                            match Unit
                            |> emptyTypeEnvironment
                            |> emptyTypeGraphIndex
                            |> typeGraphResource(root) with
                                | (resource, separate) ->
                                    match edge(ResourceFact)(0)(1)(separate) with
                                        | Some(node) -> assert("edge assertion line 135")(!resource && separate.nodeCount == 2 && node.resolvedType == alias)
                                        | None -> Ashes.IO.panic("missing separate edge")))

let recursive fresh remaining =
    if remaining == 0
    then Ashes.IO.print("fresh graph edges passed")
    else
        let root = SemTuple([SemVariable(100000 + remaining), SemList(SemString), SemVariable(100000 + remaining)])
        in
            match Unit
            |> emptyTypeEnvironment
            |> emptyTypeGraphIndex
            |> typeGraphResource(root) with
                | (resource, next) ->
                    match typeGraphUnresolved(root)(next) with
                        | (unresolved, final) ->
                            !resource && unresolved && edgeId(ResourceFact)(0)(2)(final) == Some(1) && edgeId(ResourceFact)(0)(2)(final) == edgeId(UnresolvedFact)(0)(0)(final)
                            |> assert("edge assertion line 152")
                            |> (given (_) -> fresh(remaining - 1))

match Ashes.IO.args with
    | "check" :: [] ->
        Unit
        |> checkLazy
        |> checkShortCircuit
        |> checkPolicies
        |> checkCollisions
        |> checkAlias
        |> (given (_) -> Ashes.IO.print("graph edge tests passed"))
    | "1000" :: [] -> fresh(1000)
    | "10000" :: [] -> fresh(10000)
    | _ -> Ashes.IO.panic("mode")
