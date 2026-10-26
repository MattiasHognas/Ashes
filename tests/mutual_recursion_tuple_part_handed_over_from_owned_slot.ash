// expect: 1000
type Item =
    | name: Str
    | size: Int

let recursive items (reversed: List(Item)) (state: (List(Str), Bool)) =
    match state with
        | ([], flag) -> (Ashes.Collection.List.reverse(reversed), state)
        | (token :: rest, flag) ->
            if token == "let"
            then topLet(reversed)((rest, flag))
            else (Ashes.Collection.List.reverse(reversed), (rest, flag))
and topLet (reversed: List(Item)) (state: (List(Str), Bool)) =
    match state with
        | ([], flag) -> (Ashes.Collection.List.reverse(reversed), state)
        | (name :: rest, flag) -> items(Item(name = "item " + name, size = 1) :: reversed)((rest, flag))

let parse (n: Int) =
    match items([])((["let", "x" + Ashes.Text.fromInt(n), "y"], false)) with
        | (found, (rest, _flag)) -> Ashes.Collection.List.length(found) + Ashes.Collection.List.length(rest)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + parse(n))

0
|> rounds(1000)
|> Ashes.IO.print
