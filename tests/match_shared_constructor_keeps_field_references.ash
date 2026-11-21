// expect: left1:right1/left1:right1/left2:right2
type Labels =
    | Pair(Str, Str)
    | Empty

let build n = Pair("left" + Ashes.Text.fromInt(n))("right" + Ashes.Text.fromInt(n))

let recursive choose (items: List(Labels)) =
    match items with
        | [] -> Empty
        | item :: rest ->
            match item with
                | Empty -> choose(rest)
                | Pair(_, _) -> item

let render labels =
    match labels with
        | Pair(left, right) -> left + ":" + right
        | Empty -> ""

let fromCall keep first second =
    match choose(if keep
    then [first]
    else [second]) with
        | Pair(left, right) -> left + ":" + right
        | Empty -> ""

let first = build(1)

let second = build(2)

Ashes.IO.print(fromCall(true)(first)(second) + "/" + render(first) + "/" + fromCall(false)(first)(second))
