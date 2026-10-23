// expect: 1000
type Env =
    | name: Str
    | items: List(Str)

type Dropper =
    | ids: List((Str, Int))
    | environment: Env

let recursive namedIds (names: List(Str)) (reversed: List((Str, Int))) =
    match names with
        | [] -> Ashes.Collection.List.reverse(reversed)
        | name :: rest -> namedIds(rest)((name, Ashes.Collection.List.length(reversed)) :: reversed)

let emptyEnv unit = Env(name = "standalone", items = [])

let prepare (names: List(Str)) =
    (let ids = namedIds(names)([])
    in Dropper(ids = ids, environment = emptyEnv(Unit)))

let measure (dropper: Dropper) =
    match dropper with
        | Dropper { ids = ids, environment = Env { items = items } } -> Ashes.Collection.List.length(ids) + Ashes.Collection.List.length(items)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + measure(prepare(["T" + Ashes.Text.fromInt(n), "U"])))

0
|> rounds(500)
|> Ashes.IO.print
