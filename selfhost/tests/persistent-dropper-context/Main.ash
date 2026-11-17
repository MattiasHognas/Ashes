import ProbeDroppers
import StructuralDroppersTests
import DropperViewCacheTests
import DropperAdmissibilityProjectionTests
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.TypeGraphIndex
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
import Ashes.Test as test
let check value =
    if value
    then Unit
    else test.fail("persistent dropper context mismatch")

let foo = SemNamed(0)("Foo")([])

let box argument = SemNamed(0)("Box")([argument])

let closure = SemFunction(SemInt)(SemInt)(None)

let definition name field result = ConstructorInferenceDefinition(name = name, scheme = TypeScheme(quantified = [], body = SemFunction(field)(result)(None), constraints = []), fieldNames = [])

let genericBox =
    ConstructorInferenceDefinition(name = "Box", scheme = TypeScheme(quantified = [(7, "a")], body = SemFunction(SemVariable(7))(box(SemVariable(7)))(None), constraints = []), fieldNames = [])

let definitions = [definition("Foo")(closure)(foo), genericBox]

let bodyContext (body: DropperBody) = DropperQueryContext(graphIndex = body.graphIndex, facts = body.sharedFacts)

let reopened types body =
    match openDropperBody(withDropperQueryContext(bodyContext(body))(types))(emptyDropperLabelCache)(0)(0) with
        | (_ids, opened) -> opened

let checkSharedFacts unit =
    (let types = prepareDropperTypes(definitions)
    in
        match openDropperBody(types)(emptyDropperLabelCache)(0)(0) with
            | (_ids, opened) ->
                let scalar =
                    SemInt
                    |> box
                    |> renumberPreparedType(types)
                in
                    let owned =
                        closure
                        |> box
                        |> renumberPreparedType(types)
                    in
                        match dropperAdmissibilityFactsIndexed(scalar)(opened) with
                            | (scalarFacts, scalarBody) ->
                                Ashes.Collection.Map.size(scalarBody.sharedFacts) == 1
                                |> check
                                |> (given (_) ->
                                    match scalarBody
                                    |> reopened(types)
                                    |> dropperAdmissibilityFactsIndexed(owned) with
                                        | (ownedFacts, ownedBody) ->
                                            scalarFacts != ownedFacts && Ashes.Collection.Map.size(ownedBody.sharedFacts) == 2
                                            |> check
                                            |> (given (_) ->
                                                let full = classifyHeapLayout(scalar)(opened.environment)
                                                in
                                                    let authoritative = full with containsResource = true
                                                    in
                                                        match scalar with
                                                            | SemNamed(id, _name, _args) ->
                                                                let overridden = reopened(types)(ownedBody) with layoutFacts = Ashes.Collection.Map.setInt(id)([(scalar, authoritative)])(opened.layoutFacts)
                                                                in
                                                                    match dropperAdmissibilityFactsIndexed(scalar)(overridden) with
                                                                        | (facts, _body) -> check(facts.containsResource)
                                                            | _ -> test.fail("named test type required"))))

let checkPathDecisions unit =
    (let types = prepareDropperTypes(definitions)
    in
        let named = renumberPreparedType(types)(foo)
        in
            match openDropperBody(types)(emptyDropperLabelCache)(0)(0) with
                | (_ids, opened) ->
                    match generalRcAdmissible(named)([])(opened) with
                        | (withoutPath, warmed) ->
                            match warmed
                            |> reopened(types)
                            |> generalRcAdmissible(named)([named]) with
                                | (withPath, reused) ->
                                    match reused
                                    |> reopened(types)
                                    |> generalRcAdmissible(named)([]) with
                                        | (again, _body) -> check(!withoutPath && withPath && !again))

let checkReset unit =
    (let closureTypes = prepareDropperTypes([definition("Foo")(closure)(foo)])
    in
        match openDropperBody(closureTypes)(emptyDropperLabelCache)(0)(0) with
            | (_ids, opened) ->
                match dropperAdmissibilityFactsIndexed(renumberPreparedType(closureTypes)(foo))(opened) with
                    | (oldFacts, _warmed) ->
                        let scalarTypes = prepareDropperTypes([definition("Foo")(SemInt)(foo)])
                        in
                            Ashes.Collection.Map.size(scalarTypes.sharedFacts) == 0
                            |> check
                            |> (given (_) ->
                                match openDropperBody(scalarTypes)(emptyDropperLabelCache)(0)(0) with
                                    | (_newIds, fresh) ->
                                        match dropperAdmissibilityFactsIndexed(renumberPreparedType(scalarTypes)(foo))(fresh) with
                                            | (newFacts, _body) -> check(oldFacts != newFacts)))

let contextNodeCount (context: DropperQueryContext) =
    (let graph = context.graphIndex
    in graph.nodeCount)

let checkNone n =
    (let name = "Empty" + Ashes.Text.fromInt(n)
    in
        let named = SemNamed(0)(name)([])
        in
            let types = prepareDropperTypes([definition(name)(SemInt)(named)])
            in
                match synthesizeOwnedAggregateReleaseIndexed(0)(named)(OwnedReleasePlan(deepUnique = false, constructorName = None))(types)(emptyDropperLabelCache)(1)(0)(0)(0) with
                    | (None, context) ->
                        if Ashes.Collection.Map.size(context.facts) != 1
                        then test.fail("None lost completed facts")
                        else
                            match synthesizeOwnedAggregateReleaseIndexed(0)(named)(OwnedReleasePlan(deepUnique = false, constructorName = None))(withDropperQueryContext(context)(types))(emptyDropperLabelCache)(1)(0)(0)(0) with
                                | (None, reused) -> Ashes.Collection.Map.size(reused.facts) == 1 && contextNodeCount(reused) == contextNodeCount(context)
                                | _ -> false
                    | _ -> false)

let recursive freshRounds n =
    if n == 0
    then Ashes.IO.print("fresh passed")
    else
        if checkNone(n)
        then freshRounds(n - 1)
        else test.fail("fresh context failed")

let recursive warm n types checksum =
    if n == 0
    then
        checksum
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    else
        match openDropperBody(types)(emptyDropperLabelCache)(0)(0) with
            | (_ids, opened) ->
                match dropperAdmissibilityFactsIndexed(closure
                |> box
                |> renumberPreparedType(types))(opened) with
                    | (facts, queried) ->
                        warm(n - 1)(withDropperQueryContext(bodyContext(queried))(types))(checksum + Ashes.Collection.List.length(facts.children))

match Ashes.IO.args with
    | "check" :: [] ->
        Unit
        |> runStructuralDroppersTests
        |> runDropperViewCacheTests
        |> runDropperAdmissibilityProjectionTests
        |> checkSharedFacts
        |> checkPathDecisions
        |> checkReset
        |> (given (_) -> freshRounds(1))
    | "fresh1000" :: [] -> freshRounds(1000)
    | "fresh10000" :: [] -> freshRounds(10000)
    | "warm10000" :: [] ->
        warm(10000)(prepareDropperTypes(definitions))(0)
    | "warm100000" :: [] ->
        warm(100000)(prepareDropperTypes(definitions))(0)
    | _ -> test.fail("expected check/fresh1000/fresh10000/warm10000/warm100000")
