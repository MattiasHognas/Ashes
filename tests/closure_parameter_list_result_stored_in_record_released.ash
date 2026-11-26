// expect: 420
// A list of tuples returned through a closure parameter is stored in a record: the call asks the
// closure for an arena result whether the parameter's type is inferred late or annotated, so the
// closure releases the reference-counted list it built and the record holds its own copy.
type Context =
    | width: Int
    | labelBlocks: List((Str, Int))

let recursive names (k: Int) (acc: List(Str)) =
    if k == 0
    then acc
    else names(k - 1)("end_" + Ashes.Text.fromInt(k) :: acc)

let recursive createLabelBlocks (base: Int) (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest -> (name, base) :: createLabelBlocks(base + 1)(rest)

let recursive size (entries: List((Str, Int))) (n: Int) =
    match entries with
        | [] -> n
        | (_name, b) :: rest -> size(rest)(n + b)

let buildContext (width: Int) consume =
    (let labelBlocks =
        []
        |> names(3)
        |> consume
    in Context(width = width, labelBlocks = labelBlocks))

let buildTypedContext (width: Int) (consume: List(Str) -> List((Str, Int))) =
    (let labelBlocks =
        []
        |> names(3)
        |> consume
    in Context(width = width, labelBlocks = labelBlocks))

let emitFunction (seed: Int) =
    match (seed
    |> createLabelBlocks
    |> buildContext(seed), seed + 1
    |> createLabelBlocks
    |> buildTypedContext(seed)) with
        | (cx, typed) -> size(cx.labelBlocks)(0) + size(typed.labelBlocks)(0)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + emitFunction(n))

0
|> loop(10)
|> Ashes.Text.fromInt
|> Ashes.IO.print
