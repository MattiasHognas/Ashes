// expect: 9
// A function of a mutually recursive group pipes its accumulator through a sibling lowered after it and
// on into its own next call.
type Node =
    | Leaf(Str)
    | Pick(Node, List(Str))
    | Apply(Node, List(Node))
    | Lam(Str, Node)

let recursive derive (nodes: List(Node)) (ds: List(Maybe(List(Str)))) =
    match nodes with
        | [] -> ds
        | Pick(_value, ns) :: rest -> derive(rest)(Some(ns) :: ds)
        | Apply(root, arguments) :: rest ->
            ds
            |> applied(root)(arguments)
            |> derive(rest)
        | _other :: rest -> derive(rest)(ds)
and applied (root: Node) (arguments: List(Node)) (ds: List(Maybe(List(Str)))) =
    match (root, arguments) with
        | (Lam(_parameter, body), _argument :: rest) -> applied(body)(rest)(None :: ds)
        | _ -> ds

let recursive total (ds: List(Maybe(List(Str)))) (k: Int) =
    match ds with
        | [] -> k
        | Some(_ns) :: rest -> total(rest)(k + 1)
        | None :: rest -> total(rest)(k + 2)

let probe (n: Int) =
    total(derive([Pick(Leaf("a"))(["x", Ashes.Text.fromInt(n)]), Apply(Lam("p")(Leaf("q")))([Leaf("r")]), Leaf("c")])([]))(0)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.IO.print
