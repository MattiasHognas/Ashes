// expect: 6
// A generic function given a fresh list runs as its reuse specialization; the cons over a fresh
// tuple head takes no reuse token, since the head lives in the arm's arena window.
type Context =
    | width: Int
    | labelBlocks: List((Str, Int))

let recursive createLabelBlocks base names =
    match names with
        | [] -> []
        | name :: rest -> (name, base) :: createLabelBlocks(base + 1)(rest)

let buildContext width instructions =
    (let labelBlocks =
        instructions
        |> (given (_xs) -> ["a", "b", "c"])
        |> createLabelBlocks(width)
    in
        let cx = Context(width = width, labelBlocks = labelBlocks)
        in (cx, instructions))

let recursive sizeOf (blocks: List((Str, Int))) (n: Int) =
    match blocks with
        | [] -> n
        | (_name, b) :: rest -> sizeOf(rest)(n + b)

let emitFunction (n: Int) =
    match buildContext(n)([1, 2]) with
        | (cx, _instructions) -> sizeOf(cx.labelBlocks)(0)

1
|> emitFunction
|> Ashes.IO.print
