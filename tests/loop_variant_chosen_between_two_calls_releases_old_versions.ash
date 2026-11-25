// expect: 20|800
// A tree threaded through a loop whose successor is a choice between two calls consuming it is
// placed on the reference-counted heap like one rebuilt by a single call, so each iteration
// releases the version it replaced.
type Tree =
    | Leaf
    | Node(Tree, Str, Tree)

let recursive insert (k: Str) t =
    match t with
        | Leaf -> Node(Leaf)(k)(Leaf)
        | Node(l, x, r) ->
            if k < x
            then
                Node(insert(k)(l))(x)(r)
            else
                if x < k
                then
                    r
                    |> insert(k)
                    |> Node(l)(x)
                else Node(l)(k)(r)

let put (k: Str) t = insert(k)(t)

let recursive size t =
    match t with
        | Leaf -> 0
        | Node(l, _x, r) -> size(l) + 1 + size(r)

let recursive keyBytes t =
    match t with
        | Leaf -> 0
        | Node(l, x, r) -> keyBytes(l) + Ashes.Text.byteLength(x) + keyBytes(r)

let recursive build i n m =
    if i >= n
    then m
    else
        let k = Ashes.Text.fromInt(i - i / 20 * 20)
        in
            let m2 =
                if i - i / 2 * 2 == 0
                then put(k)(m)
                else put(k + "x")(m)
            in build(i + 1)(n)(m2)

let built = build(0)(3000)(Leaf)
in
    Ashes.IO.print(Ashes.Text.fromInt(size(built)) + "|" + Ashes.Text.fromInt(keyBytes(built) * 20))
