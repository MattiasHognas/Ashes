type Derived =
    | pattern: List(Str)
    | fields: List(Str)

let derive (n: Int) = Derived(pattern = ["p" + Ashes.Text.fromInt(n)], fields = ["f" + Ashes.Text.fromInt(n)])

let tagged value (n: Int) =
    match derive(n) with
        | Derived { pattern = p, fields = f } -> [(value, f), (value, p)]

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        loop(remaining - 1)(total + Ashes.Collection.List.length(tagged(remaining)(remaining)))

0
|> loop(ROUNDS)
|> Ashes.IO.print
