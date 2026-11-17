// expect: 1

type Context =
    | labelBlocks: List((Str, Int))

let recursive createLabelBlocks base names =
    match names with
        | [] -> []
        | name :: rest -> (name, base) :: createLabelBlocks(base + 1)(rest)

let recursive sizeOf (blocks: List((Str, Int))) (n: Int) =
    match blocks with
        | [] -> n
        | (_name, b) :: rest -> sizeOf(rest)(n + b)

let buildContext width instructions =
    (let labelBlocks = createLabelBlocks(width)(["a"])
    in
        let cx = Context(labelBlocks = labelBlocks)
        in (cx, instructions))

match buildContext(1)([1]) with
    | (cx, _instructions) ->
        0
        |> sizeOf(cx.labelBlocks)
        |> Ashes.IO.print
