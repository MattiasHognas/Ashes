// expect: 9893
// A tuple joins a literal pair with a string literal beside a string of the caller's list; an arm
// takes it apart and stores both parts in a constructor it returns. The literal part is no
// reference-counted string, so the constructor holds whichever kind each part was, and the release
// of the tuple frees nothing the constructor still reads.
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
        | local :: _rest -> ("module", local)

let name (n: Int) =
    match n
    |> parts
    |> split with
        | (moduleName, local) -> Qualified(moduleName)(local)

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
