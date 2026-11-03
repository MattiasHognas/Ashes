// expect: 27
// A record literal holding a let-bound list is returned inside a tuple by a generic builder: the
// retain the escaping record takes of the list is released once the result is normalized.
type Op =
    | Label(Str)
    | Other(Int)

type Context =
    | width: Int
    | labelBlocks: List((Str, Int))
    | isEntry: Bool

let recursive collectLabelNames instructions =
    match instructions with
        | [] -> []
        | instruction :: rest ->
            match instruction with
                | Label(name) -> name :: collectLabelNames(rest)
                | _ -> collectLabelNames(rest)

let recursive createLabelBlocks base names =
    match names with
        | [] -> []
        | name :: rest -> (name, base) :: createLabelBlocks(base + 1)(rest)

let buildContext width isEntry instructions =
    (let labelBlocks =
        instructions
        |> collectLabelNames
        |> createLabelBlocks(width)
    in
        let cx = Context(width = width, labelBlocks = labelBlocks, isEntry = isEntry)
        in (cx, instructions))

let recursive sizeOf (blocks: List((Str, Int))) (n: Int) =
    match blocks with
        | [] -> n
        | (_name, b) :: rest -> sizeOf(rest)(n + b)

let label (n: Int) = "end_" + Ashes.Text.fromInt(n)

let recursive ops (n: Int) (k: Int) (acc: List(Op)) =
    if k == 0
    then acc
    else
        ops(n)(k - 1)(Label(label(n + k)) :: Other(k) :: acc)

let emitFunction (n: Int) =
    match []
    |> ops(n)(3)
    |> buildContext(n)(n > 3) with
        | (cx, instructions) -> sizeOf(cx.labelBlocks)(0) + Ashes.Collection.List.length(instructions)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + emitFunction(n))

0
|> loop(2)
|> Ashes.Text.fromInt
|> Ashes.IO.print
