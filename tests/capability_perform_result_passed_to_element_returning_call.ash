// expect: 1h,2h,3h,
capability Fetch =
    | get : Unit -> List(Str)

let first (xs: List(Str)) =
    match xs with
        | [] -> ","
        | h :: _ -> h

let probe (n: Int) =
    handle first(perform Fetch.get(Unit)) with
        | Fetch.get(u) -> resume([Ashes.Text.fromInt(n) + "h", "b"])

let recursive loop (n: Int) (acc: Str) =
    if n > 3
    then acc
    else loop(n + 1)(acc + probe(n) + ",")

""
|> loop(1)
|> Ashes.IO.print
