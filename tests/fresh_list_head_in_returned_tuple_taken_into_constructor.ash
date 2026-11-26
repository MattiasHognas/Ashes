// expect: 7786
// split returns a tuple holding the head bound out of a fresh list it matched, beside a fresh
// string; name takes the tuple apart into a constructor. The head keeps a reference of its own in
// the tuple, so the list's release at the end of the arm leaves it alive for the constructor.
type Name =
    | Qualified(Str, Str)

let parts (n: Int) = ["x" + Ashes.Text.fromInt(n), "m" + Ashes.Text.fromInt(n)]

let recursive joined (items: List(Str)) =
    match items with
        | [] -> ""
        | part :: rest -> part + joined(rest)

let split (n: Int) =
    match parts(n) with
        | [] -> ("", "")
        | local :: rest -> (joined(rest), local)

let name (n: Int) =
    match split(n) with
        | (moduleName, local) -> Qualified(moduleName)(local)

let size (value: Name) =
    match value with
        | Qualified(moduleName, local) -> Ashes.Text.length(moduleName) + Ashes.Text.length(local)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + size(name(n)))

0
|> rounds(1000)
|> Ashes.Text.fromInt
|> Ashes.IO.print
