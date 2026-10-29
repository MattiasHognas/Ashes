// expect: 150
let reverseList values =
    (let recursive go remaining result =
        match remaining with
            | [] -> result
            | head :: tail -> go(tail)(head :: result)
    in go(values)([]))

let recursive keep (parts: List(Str)) (stack: List(Str)) =
    match parts with
        | [] -> reverseList(stack)
        | head :: tail ->
            if head == ".."
            then
                match stack with
                    | [] -> keep(tail)([])
                    | _top :: rest -> keep(tail)(rest)
            else keep(tail)(head :: stack)

let mk (s: Str) = ("", Ashes.Text.split("/a/b/../c" + s)("/"))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + (match n
        |> Ashes.Text.fromInt
        |> mk with
            | (p, parts) ->
                []
                |> keep(parts)
                |> Ashes.Collection.List.length))

0
|> rounds(50)
|> Ashes.IO.print
