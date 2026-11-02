// expect: 9
external absOf(Int) -> Int = "abs@libc.so.6"

type Box =
    | Box(Int)

type Tree =
    | TAt(Tree)
    | TLeaf(Str)
    | TIf(Tree, Tree, Tree)

let positionAt builder block =
    builder + block
    |> absOf
    |> Box

let emitArm builder block body =
    block
    |> positionAt(builder)
    |> body
    |> (given (_) -> absOf(builder - block))

let firstSome (first: Maybe(Int)) (second: Maybe(Int)) =
    match first with
        | Some(_found) -> first
        | None -> second

let recursive unspan (t: Tree) =
    match t with
        | TAt(inner) -> unspan(inner)
        | other -> other

let recursive find (t: Tree) (key: Str) =
    match t with
        | TIf(c, a, b) ->
            key
            |> find(b)
            |> firstSome(find(a)(key))
            |> firstSome(match unspan(c) with
                | TLeaf(name) ->
                    if name == key
                    then Some(4)
                    else None
                | _ -> None)
        | _ -> None

let found =
    match find(TIf(TAt(TLeaf("k")))(TLeaf("a"))(TLeaf("b")))("k") with
        | Some(v) -> v
        | None -> 0

Ashes.IO.print(emitArm(7)(2)(given (_) -> 0) + found)
