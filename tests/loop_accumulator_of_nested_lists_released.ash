// expect: 40
// applied rebuilds its accumulator of `Maybe(List(Str))` elements with a call result on every
// iteration. An element holding a list kept the accumulator in the arena, where nothing released
// the results it was rebuilt from; it is placed on the reference-counted heap now.
type PipeNode =
    | PLeaf(Str)
    | PPick(PipeNode, List(Str))
    | PApply(PipeNode, List(PipeNode))
    | PLam(Str, PipeNode)

let recursive binders (acc: List(Str)) (xs: List(Str)) =
    match xs with
        | [] -> acc
        | x :: rest -> binders(x :: acc)(rest)

let recursive applied (root: PipeNode) (arguments: List(PipeNode)) (ds: List(Maybe(List(Str)))) =
    match (root, arguments) with
        | (PLam(parameter, body), _argument :: rest) -> applied(body)(rest)(Some([parameter]) :: ds)
        | _ -> ds

let recursive derive (nodes: List(PipeNode)) (ds: List(Maybe(List(Str)))) =
    match nodes with
        | [] -> ds
        | PPick(_value, ns) :: rest ->
            derive(rest)(Some(binders([])(ns)) :: ds)
        | PApply(root, arguments) :: rest ->
            ds
            |> applied(root)(arguments)
            |> derive(rest)
        | _other :: rest -> derive(rest)(ds)

let recursive total (k: Int) (ds: List(Maybe(List(Str)))) =
    match ds with
        | [] -> k
        | Some(ns) :: rest -> total(k + Ashes.Collection.List.length(ns))(rest)
        | None :: rest -> total(k)(rest)

let probe (n: Int) =
    []
    |> derive([PPick(PLeaf("ash"))(["coal", Ashes.Text.fromInt(n)]), PApply(PLam("soot")(PLeaf("coal")))([PLeaf("spark")]), PPick(n
    |> Ashes.Text.fromInt
    |> PLeaf)(["soot"])])
    |> total(0)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))
in
    0
    |> loop(10)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
