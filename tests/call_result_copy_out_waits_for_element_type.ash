// expect: file17.ash|file17.ash 3 3000
import Ashes.Collection.List.reverse
import Ashes.Text
type Split =
    | inputs: List(Str)
    | rest: List(Str)

let recursive count xs =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + count(rest)

let recursive numbered n acc =
    if n == 0
    then acc
    else numbered(n - 1)("file" + Ashes.Text.fromInt(n) + ".ash" :: acc)

let recursive churn n acc =
    if n == 0
    then acc
    else churn(n - 1)("file" + Ashes.Text.fromInt(1000 + n) + ".ash" :: acc)

let recursive split args before =
    match args with
        | [] -> (reverse(before), [])
        | "--" :: rest -> (reverse(before), rest)
        | other :: rest -> split(rest)(other :: before)

let recursive rebuild text =
    match Ashes.Text.unconsText(text) with
        | None -> ""
        | Some((head, tail)) -> head + rebuild(tail)

let describe args =
    match split(args)([]) with
        | (input :: _, programArguments) ->
            let copy = rebuild(input)
            in Split(inputs = copy + "|" + input :: [], rest = programArguments)
        | ([], programArguments) -> Split(inputs = [], rest = programArguments)

let parsed =
    []
    |> numbered(17)
    |> (given (files) -> "--" :: files)
    |> (given (all) -> "file17.ash" :: all)
    |> describe

let noise = churn(3000)([])

match parsed with
    | Split { inputs = shown :: [], rest = rest } ->
        Ashes.IO.print(shown + " " + Ashes.Text.fromInt(count(rest) - 14) + " " + Ashes.Text.fromInt(count(noise)))
    | _ -> Ashes.IO.print("unexpected")
