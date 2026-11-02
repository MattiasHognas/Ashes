// expect: 2=y;r=p;a=2,x; 1=y;r=p;a=1,x;
// A function of a mutually recursive group conses a fresh tuple holding a helper's list onto the
// accumulator it passes to its own next call, and its partner conses a tuple of a literal list.
type Node =
    | Leaf(Str)
    | Pick(Node, List(Str))
    | Apply(Node, List(Node))
    | Lam(Str, Node)

let recursive binders (acc: List(Str)) (xs: List(Str)) =
    match xs with
        | [] -> acc
        | x :: rest -> binders(x :: acc)(rest)

let recursive derive (nodes: List(Node)) (ds: List((Node, List(Str)))) =
    match nodes with
        | [] -> ds
        | Pick(value, ns) :: rest -> derive(rest)((value, binders([])(ns)) :: ds)
        | Apply(root, arguments) :: rest ->
            ds
            |> applied(root)(arguments)
            |> derive(rest)
        | _other :: rest -> derive(rest)(ds)
and applied (root: Node) (arguments: List(Node)) (ds: List((Node, List(Str)))) =
    match (root, arguments) with
        | (Lam(parameter, body), argument :: rest) -> applied(body)(rest)((argument, [parameter]) :: ds)
        | _ -> ds

let show (node: Node) =
    match node with
        | Leaf(s) -> s
        | _ -> "?"

let recursive render (ds: List((Node, List(Str)))) =
    match ds with
        | [] -> ""
        | (v, ns) :: rest -> show(v) + "=" + Ashes.Text.join(",")(ns) + ";" + render(rest)

let start (f: List((Node, List(Str))) -> List((Node, List(Str)))) = f([])

let probe (n: Int) =
    [Pick(Leaf("a"))(["x", Ashes.Text.fromInt(n)]), Apply(Lam("p")(Leaf("q")))([Leaf("r")]), Leaf("c"), Pick(n
    |> Ashes.Text.fromInt
    |> Leaf)(["y"])]
    |> derive
    |> start
    |> render

let recursive loop (n: Int) (acc: Str) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n) + " ")

""
|> loop(2)
|> Ashes.IO.print
