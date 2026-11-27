// expect: 7786
// A match arm over a fresh reference-counted tuple whose body is a nested match building a
// constructor from the tuple's parts in each arm, the outer match a call argument: each inner arm
// copies its constructor off the tuple the outer arm releases, and the call releases the copy.
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
        | (mm, ll) ->
            match n % 2 with
                | 0 -> Qualified(mm)(ll)
                | _ -> Qualified(ll)(mm))

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
