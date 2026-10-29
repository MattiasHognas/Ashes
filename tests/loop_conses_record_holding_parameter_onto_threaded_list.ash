// expect: 1000
type Facts =
    | defs: List(Str)
    | names: List(Str)

type Site =
    | target: Str
    | index: Int
    | creator: Facts
    | source: Int

let recursive addSites (label: Str) (index: Int) (count: Int) (facts: Facts) sites unresolvable =
    if index >= count
    then (sites, unresolvable)
    else
        if index == 1
        then addSites(label)(index + 1)(count)(facts)(sites)((label, index) :: unresolvable)
        else addSites(label)(index + 1)(count)(facts)(Site(target = label, index = index, creator = facts, source = index) :: sites)(unresolvable)

let recursive inBody (items: List(Int)) (label: Str) (facts: Facts) sites unresolvable =
    match items with
        | [] -> (sites, unresolvable)
        | item :: rest ->
            if item > 0
            then
                match addSites(label + Ashes.Text.fromInt(item))(0)(item)(facts)(sites)(unresolvable) with
                    | (nextSites, nextUnresolvable) -> inBody(rest)(label)(facts)(nextSites)(nextUnresolvable)
            else inBody(rest)(label)(facts)(sites)(unresolvable)

let factsOf (items: List(Int)) =
    Facts(defs = Ashes.Collection.List.map(given (x) -> Ashes.Text.fromInt(x))(items), names = ["a" + Ashes.Text.fromInt(Ashes.Collection.List.length(items))])

let recursive collect (functions: List(List(Int))) sites unresolvable =
    match functions with
        | [] -> (sites, unresolvable)
        | fn :: tail ->
            match inBody(fn)("f")(factsOf(fn))(sites)(unresolvable) with
                | (nextSites, nextUnresolvable) -> collect(tail)(nextSites)(nextUnresolvable)

let probe (n: Int) =
    match collect([[1, 2, 3], [n, 0, 2]])([])([]) with
        | ([], []) -> 0
        | _ -> 1

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + probe(n))

0
|> rounds(1000)
|> Ashes.IO.print
