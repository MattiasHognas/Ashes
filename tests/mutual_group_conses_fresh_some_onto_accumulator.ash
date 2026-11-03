// expect: -;2,x; -;1,x;
// A function of a mutually recursive group conses `Some` of a helper's list onto the accumulator it
// passes to its own next call, and its partner conses `None`.
type Node =
    | Leaf(Str)
    | Pick(Node, List(Str))
    | Apply(Node, List(Node))
    | Lam(Str, Node)

let recursive binders (acc: List(Str)) (xs: List(Str)) =
    match xs with
        | [] -> acc
        | x :: rest -> binders(x :: acc)(rest)

let recursive derive (nodes: List(Node)) (ds: List(Maybe(List(Str)))) =
    match nodes with
        | [] -> ds
        | Pick(_value, ns) :: rest ->
            derive(rest)(Some(binders([])(ns)) :: ds)
        | Apply(root, arguments) :: rest ->
            ds
            |> applied(root)(arguments)
            |> derive(rest)
        | _other :: rest -> derive(rest)(ds)
and applied (root: Node) (arguments: List(Node)) (ds: List(Maybe(List(Str)))) =
    match (root, arguments) with
        | (Lam(_parameter, body), _argument :: rest) -> applied(body)(rest)(None :: ds)
        | _ -> ds

let recursive render (ds: List(Maybe(List(Str)))) =
    match ds with
        | [] -> ""
        | Some(ns) :: rest -> Ashes.Text.join(",")(ns) + ";" + render(rest)
        | None :: rest -> "-;" + render(rest)

let probe (n: Int) =
    []
    |> derive([Pick(Leaf("a"))(["x", Ashes.Text.fromInt(n)]), Apply(Lam("p")(Leaf("q")))([Leaf("r")]), Leaf("c")])
    |> render

let recursive loop (n: Int) (acc: Str) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n) + " ")

""
|> loop(2)
|> Ashes.IO.print
