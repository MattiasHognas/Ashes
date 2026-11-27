// expect: 7786
// A helper hands a call the arena constructor a match arm builds from the parts of a fresh
// reference-counted tuple. The arm copies the constructor off the tuple it releases, and the call
// releases that copy once it returns, so nothing stays live per call.
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

let probe (n: Int) =
    size(match split(n) with
        | (mm, ll) -> Qualified(mm)(ll))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let v = probe(n)
        in rounds(n - 1)(total + v)

0
|> rounds(1000)
|> Ashes.Text.fromInt
|> Ashes.IO.print
