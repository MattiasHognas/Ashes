// expect: 3606
type Node =
    | At(Int, Node)
    | Lam(Str, Node)
    | Leaf(Int)

let recursive firstParts node =
    match node with
        | At(_span, inner) -> firstParts(inner)
        | Lam(parameter, body) -> Some((parameter, body))
        | _ -> None

let recursive laterParts node =
    match node with
        | At(_span, inner) -> laterParts(inner)
        | Lam(parameter, body) -> Some((parameter, body))
        | _ -> None

let recursive depth (node: Node) =
    match node with
        | At(_span, inner) -> depth(inner)
        | Lam(_parameter, body) -> 1 + depth(body)
        | Leaf(n) -> n

let measure (node: Node) =
    match (firstParts(node), laterParts(node)) with
        | (Some((a, first)), Some((b, later))) -> Ashes.Text.byteLength(a) + Ashes.Text.byteLength(b) + depth(first) + depth(later)
        | _ -> 0

let recursive build (k: Int) (n: Int) =
    if k == 0
    then Leaf(n)
    else
        n
        |> build(k - 1)
        |> Lam(Ashes.Text.fromInt(n + k))
        |> At(k)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + measure(build(3)(n % 7)))
in
    0
    |> loop(300)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
