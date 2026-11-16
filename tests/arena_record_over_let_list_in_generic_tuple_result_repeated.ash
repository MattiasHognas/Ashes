// expect: 100020000

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
    (let labelBlocks = createLabelBlocks(width)(["a", "b"])
    in
        let cx = Context(labelBlocks = labelBlocks)
        in (cx, instructions))

let recursive rounds (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        match buildContext(n)([n]) with
            | (cx, _instructions) -> rounds(n - 1)(acc + sizeOf(cx.labelBlocks)(0))

0
|> rounds(10000)
|> Ashes.IO.print
