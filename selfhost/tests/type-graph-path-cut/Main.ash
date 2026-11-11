import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeGraphSearch
let sameName left right =
    match (left, right) with
        | (SemNamed(a, n, _xs), SemNamed(b, m, _ys)) -> a == b && n == m
        | _ -> false

let recursive inPath value path =
    match path with
        | [] -> false
        | head :: rest -> sameName(value)(head) || inPath(value)(rest)

let identity value = value

let target value = value == SemOpaque("Hit")

let recursive oldSearch children value path =
    if target(value)
    then true
    else
        match value with
            | SemNamed(_id, _name, _args) ->
                if inPath(value)(path)
                then false
                else
                    oldAny(children)(children(value))(value :: path)
            | _ ->
                oldAny(children)(children(value))(path)
and oldAny children values path =
    match values with
        | [] -> false
        | value :: rest -> oldSearch(children)(value)(path) || oldAny(children)(rest)(path)

let recursive power n =
    if n == 0
    then 1
    else 2 * power(n - 1)

let node n =
    SemNamed(n / 2)("N" + Ashes.Text.fromInt(n / 2))([if n % 2 == 0
    then SemInt
    else SemBool])

let nodeIndex id args =
    id * 2 + (if args == [SemInt]
    then 0
    else 1)

let recursive graphEdges graph from next =
    if next == 4
    then []
    else
        if graph / power(from * 4 + next) % 2 == 1
        then node(next) :: graphEdges(graph)(from)(next + 1)
        else graphEdges(graph)(from)(next + 1)

let graphChildren graph value =
    match value with
        | SemNamed(id, _name, args) ->
            let index = nodeIndex(id)(args)
            in
                Ashes.Collection.List.append(graphEdges(graph)(index)(0))(if index == 3
                then [SemOpaque("Hit")]
                else [])
        | _ -> []

let assert value =
    if value
    then Unit
    else Ashes.IO.panic("path-cut mismatch")

let recursive roots graph root =
    if root == 4
    then Unit
    else
        oldSearch(graphChildren(graph))(node(root))([]) == typeGraphAnyWithPathCuts(identity)(target)(graphChildren(graph))(node(root))
        |> assert
        |> (given (_) -> roots(graph)(root + 1))

let recursive graphs graph =
    if graph == 65536
    then Ashes.IO.print("262144 comparisons ok")
    else
        0
        |> roots(graph)
        |> (given (_) -> graphs(graph + 1))

let diamond n = SemNamed(100 + n)("D" + Ashes.Text.fromInt(n))([])

let adversaryChildren depth value =
    match value with
        | SemNamed(90, "Root", args) ->
            if args == [SemInt]
            then [SemNamed(90)("Root")([SemList(SemInt)]), diamond(depth)]
            else [SemOpaque("Hit")]
        | SemNamed(id, _name, []) ->
            if id == 100
            then []
            else [diamond(id - 101), diamond(id - 101)]
        | _ -> []

let countedChildren value =
    "visit"
    |> Ashes.IO.print
    |> (given (_) -> adversaryChildren(14)(value))

let root = SemNamed(90)("Root")([SemInt])

let recursive plateau rounds =
    if rounds == 0
    then Ashes.IO.print("done")
    else
        typeGraphAny(identity)(target)(adversaryChildren(4))(root) == None && !typeGraphAnyWithPathCuts(identity)(target)(adversaryChildren(4))(root)
        |> assert
        |> (given (_) -> plateau(rounds - 1))

let fork n = SemNamed(1000 + n * 3)("Fork" + Ashes.Text.fromInt(n))([])

let forkChildren value =
    match value with
        | SemNamed(90, "Root", args) ->
            if args == [SemInt]
            then [SemNamed(90)("Root")([SemList(SemInt)]), fork(10)]
            else [SemOpaque("Hit")]
        | SemNamed(id, _name, []) ->
            if id == 1000
            then []
            else
                if (id - 1000) % 3 == 0
                then [SemNamed(id + 1)("L" + Ashes.Text.fromInt(id))([]), SemNamed(id + 2)("R" + Ashes.Text.fromInt(id))([])]
                else
                    if (id - 1000) % 3 == 1
                    then [fork((id - 1001) / 3 - 1)]
                    else [fork((id - 1002) / 3 - 1)]
        | _ -> []

let countedForkChildren value =
    "visit"
    |> Ashes.IO.print
    |> (given (_) -> forkChildren(value))

// Collisions in either nominal component must not discard a distinct reachable node.
// Equal nominal keys at different generic arguments must still compare the full type.
let bucketCollisionChildren (value: SemanticType) =
    match value with
        | SemTuple(values) -> values
        | SemNamed(7, "Different", _args) -> [SemOpaque("Hit")]
        | SemNamed(8, "Shared", _args) -> [SemOpaque("Hit")]
        | SemNamed(7, "Shared", SemBool :: []) -> [SemOpaque("Hit")]
        | _ -> []

let bucketCollision (hit: SemanticType) =
    (let start = SemTuple([SemNamed(7)("Shared")([SemInt]), hit])
    in assert(oldSearch(bucketCollisionChildren)(start)([]) && typeGraphAnyWithPathCuts(identity)(target)(bucketCollisionChildren)(start)))

let bucketCollisions (_unit: Unit) =
    [SemInt]
    |> SemNamed(7)("Different")
    |> bucketCollision
    |> (given (_) ->
        [SemInt]
        |> SemNamed(8)("Shared")
        |> bucketCollision)
    |> (given (_) ->
        [SemBool]
        |> SemNamed(7)("Shared")
        |> bucketCollision)
    |> (given (_) -> Ashes.IO.print("bucket collisions ok"))

match Ashes.IO.args with
    | "buckets" :: [] -> bucketCollisions(Unit)
    | "fork-old" :: [] -> assert(!oldSearch(countedForkChildren)(root)([]))
    | "fork-new" :: [] -> assert(!typeGraphAnyWithPathCuts(identity)(target)(countedForkChildren)(root))
    | "graphs" :: [] -> graphs(0)
    | "old" :: [] -> assert(!oldSearch(countedChildren)(root)([]))
    | "new" :: [] -> assert(!typeGraphAnyWithPathCuts(identity)(target)(countedChildren)(root))
    | "1000" :: [] -> plateau(1000)
    | "10000" :: [] -> plateau(10000)
    | _ -> Ashes.IO.panic("mode required")
