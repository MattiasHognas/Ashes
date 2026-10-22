// A recursive builder conses a tuple per step onto its own result, built from the fields of a record
// a call returned and holding a `None` of a type nothing reaches. Read with that type as `Never`, the
// result is one of the contract's types, so the builder owns it and releases the record behind it.
// expect: 6
type Node =
    | Leaf(Str)
    | Group(List(Node))

type Derived =
    | pattern: Node
    | fields: List(Str)

let derive (n: Int) = Derived(pattern = Group([Leaf("p" + Ashes.Text.fromInt(n))]), fields = ["f" + Ashes.Text.fromInt(n)])

let recursive cases (n: Int) =
    if n == 0
    then []
    else
        match derive(n) with
            | Derived { pattern = p, fields = f } -> (Group([p]), f, None) :: cases(n - 1)

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        loop(remaining - 1)(total + Ashes.Collection.List.length(cases(3)))

0
|> loop(2)
|> Ashes.IO.print
