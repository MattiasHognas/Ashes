// expect: 5838
// An arm takes apart a tuple joined from a literal and a fresh one and returns one of two
// constructors over its parts through an `if`. Either constructor outlives the tuple's release at
// the end of the arm, so it is built reference-counted with strings of its own.
let parts (n: Int) = ["x" + Ashes.Text.fromInt(n), "m" + Ashes.Text.fromInt(n)]

type Name =
    | Plain(Str)
    | Qualified(Str, Str)

let recursive joined (parts: List(Str)) =
    match parts with
        | [] -> ""
        | part :: rest -> part + joined(rest)

let split (items: List(Str)) =
    match items with
        | [] -> ("", "")
        | local :: rest -> (joined(rest), local)

let name (n: Int) =
    match n
    |> parts
    |> split with
        | (moduleName, local) ->
            if n % 2 == 0
            then Plain(local)
            else Qualified(moduleName)(local)

let size (value: Name) =
    match value with
        | Plain(text) -> Ashes.Text.length(text)
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
