import ProbeDroppers
import FrozenDecision
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.HeapLayoutClassification
import Ashes.Collection.List as list
import Ashes.Collection.Map as map
let check condition =
    if condition
    then Unit
    else Ashes.IO.panic("dropper decision differs")

let node n = SemNamed(n)("Node" + Ashes.Text.fromInt(n))([])

let recursive arrow fields result =
    match fields with
        | [] -> result
        | head :: rest ->
            SemFunction(head)(arrow(rest)(result))(None)

let definition name result fields = ConstructorInferenceDefinition(name = name, scheme = TypeScheme(quantified = [], body = arrow(fields)(result), constraints = []), fieldNames = [])

let openBody definitions =
    match openDropperBody(prepareDropperTypes(definitions))(emptyDropperLabelCache)(0)(0) with
        | (_ids, body) -> body

let child semanticType dropKind = HeapLayoutChild(constructorName = None, fieldIndex = 0, childType = semanticType, dropKind = dropKind, copyKind = NoStructuralCopy)

let closure = SemFunction(SemInt)(SemInt)(None)

let baseBody unit =
    openBody([definition("Node0")(node(0))([]), definition("Node1")(node(1))([]), definition("Node2")(node(2))([])])

let overlay semanticType children resource unresolved (body: DropperBody) =
    (let scalar = classifyHeapLayout(SemInt)(body.environment)
    in
        let facts = scalar with children = children, containsResource = resource, containsUnresolvedType = unresolved
        in
            match semanticType with
                | SemNamed(id, _name, _args) ->
                    let prior =
                        match map.getInt(id)(body.layoutFacts) with
                            | None -> []
                            | Some(entries) ->
                                list.filter(given (entry) ->
                                    match entry with
                                        | (key, _facts) -> key != semanticType)(entries)
                    in body with layoutFacts = map.setInt(id)((semanticType, facts) :: prior)(body.layoutFacts)
                | _ -> Ashes.IO.panic("named overlay required"))

let compareDecision semanticType path body =
    match frozenGeneralRcAdmissible(semanticType)(path)(body) with
        | (expected, _frozen) ->
            match generalRcAdmissible(semanticType)(path)(body) with
                | (actual, queried) ->
                    if expected == actual
                    then queried
                    else Ashes.IO.panic("frozen decision mismatch")

let recursive power n =
    if n == 0
    then 1
    else 2 * power(n - 1)

let edges mask source =
    [0, 1, 2]
    |> list.filter(given (target) -> mask / power(source * 3 + target) % 2 == 1)
    |> list.map(given (target) ->
        child(node(target))(DropAdt))

let graph mask failure body =
    body
    |> overlay(node(0))(if failure == 1
    then child(closure)(DropClosure) :: edges(mask)(0)
    else edges(mask)(0))(false)(false)
    |> overlay(node(1))(edges(mask)(1))(failure == 2)(false)
    |> overlay(node(2))(edges(mask)(2))(false)(failure == 3)

let recursive paths root body remaining =
    match remaining with
        | [] -> Unit
        | path :: rest ->
            body
            |> compareDecision(root)(path)
            |> (given (_) -> paths(root)(body)(rest))

let recursive roots body remaining =
    match remaining with
        | [] -> Unit
        | root :: rest ->
            [[], [node(0)], [node(1)], [node(2)]]
            |> paths(root)(body)
            |> (given (_) -> roots(body)(rest))

let recursive failures mask failure body =
    if failure == 4
    then Unit
    else
        [node(0), node(1), node(2)]
        |> roots(graph(mask)(failure)(body))
        |> (given (_) -> failures(mask)(failure + 1)(body))

let recursive graphs mask body =
    if mask == 512
    then Ashes.IO.print("24576 graph decisions passed")
    else
        body
        |> failures(mask)(0)
        |> (given (_) -> graphs(mask + 1)(body))

let aliasChecks unit =
    (let first = SemNamed(0)("Alias")([SemInt])
    in
        let second = SemNamed(1)("Alias")([SemInt])
        in
            let root = SemTuple([first, second])
            in
                let body =
                    [definition("Alias")(first)([])]
                    |> openBody
                    |> overlay(first)([])(false)(false)
                    |> overlay(second)([child(closure)(DropClosure)])(false)(false)
                in
                    body
                    |> compareDecision(root)([])
                    |> (given (_) -> compareDecision(second)([first])(body))
                    |> (given (_) ->
                        let bare = SemNamed(2)("Alias(Int)")([])
                        in
                            body
                            |> overlay(bare)([child(closure)(DropClosure)])(false)(false)
                            |> compareDecision(bare)([first]))
                    |> (given (_) ->
                        let opaqueArgument = SemNamed(3)("Alias")([SemOpaque("T")])
                        in
                            let namedArgument = SemNamed(4)("Alias")([SemNamed(0)("T")([])])
                            in
                                body
                                |> overlay(opaqueArgument)([])(false)(false)
                                |> overlay(namedArgument)([child(closure)(DropClosure)])(false)(false)
                                |> compareDecision(SemTuple([opaqueArgument, namedArgument]))([]))
                    |> (given (_) ->
                        let succeeded = compareDecision(first)([])(body)
                        in
                            succeeded
                            |> overlay(first)([child(closure)(DropClosure)])(false)(false)
                            |> compareDecision(first)([]))
                    |> (given (_) -> Ashes.IO.print("alias and override decisions passed")))

let shapeChecks unit =
    (let body = baseBody(Unit)
    in
        [[], [node(0)]]
        |> paths(SemTuple([SemString, 0
        |> node
        |> SemList, SemBytes, SemBigInt]))(body)
        |> (given (_) ->
            body
            |> overlay(node(0))([child(SemInt)(UnsupportedChildDrop)])(false)(false)
            |> compareDecision(node(0))([]))
        |> (given (_) ->
            compareDecision(SemNamed(22)("Missing")([]))([SemNamed(22)("Missing")([])])(body))
        |> (given (_) -> Ashes.IO.print("shape decisions passed")))

let realFactsChecks unit =
    (let body =
        openBody([definition("Node0")(node(0))([SemString])])
    in
        match generalRcAdmissible(node(0))([])(body) with
            | (accepted, warmed) ->
                accepted && map.size(warmed.sharedFacts) > 0
                |> check
                |> (given (_) ->
                    let overridden =
                        overlay(node(0))([child(closure)(DropClosure)])(false)(false)(warmed)
                    in
                        match generalRcAdmissible(node(0))([])(overridden) with
                            | (changed, queried) ->
                                !changed
                                |> check
                                |> (given (_) ->
                                    compareDecision(node(0))([])(queried))
                                |> (given (checked) ->
                                    compareDecision(node(0))([node(0)])(checked)))
                |> (given (_) -> Ashes.IO.print("warm shared facts honor local override")))

let recursive diamondDefinitions level =
    if level < 0
    then []
    else
        definition("Node" + Ashes.Text.fromInt(level))(node(level))(if level == 0
        then [SemString]
        else [node(level - 1), node(level - 1)]) :: diamondDefinitions(level - 1)

let diamond depth =
    (let body =
        depth
        |> diamondDefinitions
        |> list.reverse
        |> openBody
    in
        match dropperAdmissibilityFactsIndexed(node(depth))(body) with
            | (facts, queried) ->
                if list.length(facts.children) == (if depth == 0
                then 1
                else 2)
                then queried
                else Ashes.IO.panic("diamond constructor fields missing"))

let recursive warmRounds old n depth body =
    if n == 0
    then Ashes.IO.print("warm passed")
    else
        match if old
        then
            frozenGeneralRcAdmissible(node(depth))([])(body)
        else
            generalRcAdmissible(node(depth))([])(body) with
            | (true, queried) -> warmRounds(old)(n - 1)(depth)(queried)
            | (false, _body) -> Ashes.IO.panic("diamond rejected")

let freshOnce n =
    (let named = SemNamed(0)("Fresh" + Ashes.Text.fromInt(n))([])
    in
        match [definition("Fresh" + Ashes.Text.fromInt(n))(named)([SemString])]
        |> openBody
        |> generalRcAdmissible(named)([]) with
            | (answer, _body) -> answer)

let recursive freshRounds n =
    if n == 0
    then Ashes.IO.print("fresh passed")
    else
        if freshOnce(n)
        then freshRounds(n - 1)
        else Ashes.IO.panic("fresh rejected")

let number value =
    match Ashes.Text.parseInt(value) with
        | Ok(n) -> n
        | Error(message) -> Ashes.IO.panic(message)

match Ashes.IO.args with
    | "check" :: [] ->
        Unit
        |> baseBody
        |> graphs(0)
        |> (given (_) -> aliasChecks(Unit))
        |> (given (_) -> shapeChecks(Unit))
        |> (given (_) -> realFactsChecks(Unit))
    | "fresh" :: count :: [] ->
        count
        |> number
        |> freshRounds
    | "warm" :: count :: [] ->
        4
        |> diamond
        |> warmRounds(false)(number(count))(4)
    | "scale" :: version :: depth :: count :: [] ->
        depth
        |> number
        |> diamond
        |> warmRounds(version == "old")(number(count))(number(depth))
    | _ -> Ashes.IO.panic("expected check/fresh/warm/scale")
