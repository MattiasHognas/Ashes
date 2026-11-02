// expect: 30 51 30
let recursive addNew (candidates: List(Str)) (names: List(Str)) =
    match candidates with
        | [] -> names
        | candidate :: rest -> addNew(rest)(candidate :: names)

let recursive derivePass (derivations: List(Str)) (names: List(Str)) =
    match derivations with
        | [] -> names
        | value :: rest ->
            derivePass(rest)(if value == "a"
            then addNew([value])(names)
            else names)

let recursive deriveMatch (derivations: List(Str)) (names: List(Str)) =
    match derivations with
        | [] -> names
        | value :: rest ->
            deriveMatch(rest)(match value == "a" with
                | true -> addNew([value])(names)
                | false -> names)

let probe (n: Int) =
    ["x"]
    |> derivePass(["a", "b" + Ashes.Text.fromInt(n), "a"])
    |> Ashes.Collection.List.length

let readAfter (n: Int) =
    (let start = ["x" + Ashes.Text.fromInt(n)]
    in
        Ashes.Collection.List.length(derivePass(["a", "b", "a"])(start)) + Ashes.Text.byteLength(Ashes.Collection.List.foldLeft(given (acc) ->
            given (s) -> acc + s)("")(start)))

let elseFirst (n: Int) =
    ["x"]
    |> deriveMatch(["b" + Ashes.Text.fromInt(n), "a", "b", "a"])
    |> Ashes.Collection.List.length

let recursive sum (f: Int -> Int) (n: Int) (acc: Int) =
    if n == 0
    then acc
    else sum(f)(n - 1)(acc + f(n))

Ashes.IO.print(Ashes.Text.fromInt(sum(probe)(10)(0)) + " " + Ashes.Text.fromInt(sum(readAfter)(10)(0)) + " " + Ashes.Text.fromInt(sum(elseFirst)(10)(0)))
