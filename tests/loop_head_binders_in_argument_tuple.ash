// expect: 3984
type Item =
    | ItemLet(Str, Str)

let recursive prepareGroup (bindings: List((Str, Str))) (total: Int) =
    match bindings with
        | [] -> total
        | (name, value) :: rest -> prepareGroup(rest)(total + Ashes.Text.byteLength(name) + Ashes.Text.byteLength(value))

let recursive lowerItems (items: List(Item)) (total: Int) =
    match items with
        | [] -> total
        | ItemLet(name, value) :: rest ->
            total
            |> prepareGroup([(name, value)])
            |> lowerItems(rest)

let recursive itemsOf (k: Int) (n: Int) =
    if k == 0
    then []
    else
        ItemLet(Ashes.Text.fromInt(1000 + n + k))(Ashes.Text.fromInt(n)) :: itemsOf(k - 1)(n)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + lowerItems(itemsOf(2)(n))(0))
in
    0
    |> loop(300)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
