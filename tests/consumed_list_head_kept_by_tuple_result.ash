// expect: 3450
let splitQualified (parts: List(Str)) =
    match parts with
        | [] -> ("", "")
        | name :: _ -> ("x", name)

let splitRest (parts: List(Str)) =
    match parts with
        | [] -> ("", [])
        | name :: rest -> (name, rest)

let recursive build (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else build(n - 1)(Ashes.Text.fromInt(n) :: acc)

let recursive count (xs: List(Str)) (total: Int) =
    match xs with
        | [] -> total
        | x :: rest -> count(rest)(total + Ashes.Text.byteLength(x))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let named =
            match []
            |> build(3)
            |> splitQualified with
                | (a, b) -> Ashes.Text.byteLength(a) + Ashes.Text.byteLength(b)
        in
            match []
            |> build(4)
            |> splitRest with
                | (a, rest) -> rounds(n - 1)(total + named + Ashes.Text.byteLength(a) + count(rest)(0))

0
|> rounds(575)
|> Ashes.IO.print
