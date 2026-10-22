// expect: 11 11 4 k1,k2,k3,k4

type T =
    | Leaf
    | Node(T, Int, T)

let recursive sumTree (acc: Int) (t: T) =
    match t with
        | Leaf -> acc
        | Node(left, value, right) ->
            let afterLeft = sumTree(acc)(left)
            in sumTree(afterLeft + value)(right)

let recursive keysOf (n: Int) (keys: MapTree(Str, Int)) =
    if n == 0
    then keys
    else
        keys
        |> Ashes.Collection.Map.setStr("k" + Ashes.Text.fromInt(n))(n)
        |> keysOf(n - 1)

let tree =
    Leaf
    |> Node(Leaf)(3)
    |> Node(Node(Node(Leaf)(5)(Leaf))(1)(Leaf))(2)

let keys = keysOf(4)(Ashes.Collection.Map.empty)

let total =
    Ashes.Collection.Map.foldLeft(given (acc: Int) ->
        given (_key: Str) ->
            given (value: Int) -> acc + value)(1)(keys)

let names =
    keys
    |> Ashes.Collection.Map.toList
    |> Ashes.Collection.List.map(given (entry: (Str, Int)) ->
        match entry with
            | (key, _value) -> key)
    |> Ashes.Collection.List.reverse

Ashes.IO.print(Ashes.Text.fromInt(sumTree(0)(tree)) + " " + Ashes.Text.fromInt(total) + " " + Ashes.Text.fromInt(Ashes.Collection.List.length(names)) + " " + Ashes.Text.join(",")(names))
