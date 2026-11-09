// expect: 6
// A non-tail producer conses a tuple of a match-bound string onto its own result; the tuple is
// reference-counted and stores an owned copy of the string.
let recursive blocks (base: Int) (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest -> (name, base) :: blocks(base + 1)(rest)

let recursive size (entries: List((Str, Int))) (n: Int) =
    match entries with
        | [] -> n
        | (_name, b) :: rest -> size(rest)(n + b)

let recursive names (n: Int) (k: Int) (acc: List(Str)) =
    if k == 0
    then acc
    else names(n)(k - 1)("x" + Ashes.Text.fromInt(n + k) :: acc)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + size([]
        |> names(n)(1)
        |> blocks(n))(0))

0
|> loop(3)
|> Ashes.IO.print
