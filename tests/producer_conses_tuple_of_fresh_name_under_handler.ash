// expect: 18
// A recursive producer in a program with a handler conses a tuple of a fresh string and a tree its
// tuple cannot own onto its own result.
capability Tick =
    | get : Unit -> Int

type Tree =
    | Leaf(Int)
    | Node(Str, Tree)

let recursive build (depth: Int) (n: Int) =
    if depth == 0
    then Leaf(n)
    else
        n
        |> build(depth - 1)
        |> Node(Ashes.Text.fromInt(depth))

let recursive weight (t: Tree) =
    match t with
        | Leaf(n) -> n
        | Node(_s, rest) -> 1 + weight(rest)

let recursive bindingsOf (k: Int) (n: Int) =
    if k == 0
    then []
    else (Ashes.Text.fromInt(n + k), build(2)(n)) :: bindingsOf(k - 1)(n)

let recursive total (bs: List((Str, Tree))) (acc: Int) =
    match bs with
        | [] -> acc
        | (name, t) :: rest -> total(rest)(acc + Ashes.Text.byteLength(name) + weight(t))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + total(bindingsOf(2)(n))(0))
in
    (handle loop(2)(perform Tick.get(Unit)) with
        | Tick.get(u) -> resume(0))
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
