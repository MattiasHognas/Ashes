// expect: 3
// A record literal holding a let-bound list is returned inside a tuple: the escaping record retains
// the list it borrows, so the let's release leaves the caller's copy readable.
type Context =
    | width: Int
    | labelBlocks: List((Str, Int))

let recursive createLabelBlocks base names =
    match names with
        | [] -> []
        | name :: rest -> (name, base) :: createLabelBlocks(base + 1)(rest)

let recursive collect (xs: List(Str)) =
    match xs with
        | [] -> []
        | x :: rest -> x :: collect(rest)

let buildContext width instructions =
    (let labelBlocks =
        instructions
        |> collect
        |> createLabelBlocks(width)
    in
        let cx = Context(width = width, labelBlocks = labelBlocks)
        in (cx, instructions))

let recursive sizeOf (blocks: List((Str, Int))) (n: Int) =
    match blocks with
        | [] -> n
        | (_name, b) :: rest -> sizeOf(rest)(n + b)

let emitFunction (n: Int) =
    match buildContext(n)(["a", "b"]) with
        | (cx, _instructions) -> sizeOf(cx.labelBlocks)(0)

1
|> emitFunction
|> Ashes.IO.print
