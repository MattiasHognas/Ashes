type Pat =
    | PVar(Str)
    | PTuple(List(Pat))

type Derived =
    | pattern: Pat
    | fields: List(Str)

let derive (n: Int) = Derived(pattern = PTuple([PVar("p" + Ashes.Text.fromInt(n))]), fields = ["f" + Ashes.Text.fromInt(n)])

let recursive cases (n: Int) (tag: Str) =
    if n == 0
    then []
    else
        match derive(n) with
            | Derived { pattern = p, fields = f } -> (PTuple([p]), f, None) :: cases(n - 1)(tag)

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        loop(remaining - 1)(total + Ashes.Collection.List.length(cases(3)("t")))

0
|> loop(ROUNDS)
|> Ashes.IO.print
