// expect: 15
// A list literal of strings a call just returned is passed to a builder that keeps them in a record
// inside its result tuple; the literal's references to the strings are released after the call.
type Context2465 =
    | width: Int
    | labelBlocks: List((Str, Int))

let recursive blocks2465 (base: Int) (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest -> (name, base) :: blocks2465(base + 1)(rest)

let build2465 (width: Int) (names: List(Str)) (instructions: List(Int)) =
    (let labelBlocks = blocks2465(width)(names)
    in
        let cx = Context2465(width = width, labelBlocks = labelBlocks)
        in (cx, width))

let recursive size2465 (entries: List((Str, Int))) (n: Int) =
    match entries with
        | [] -> n
        | (_name, b) :: rest -> size2465(rest)(n + b)

let label2465 (n: Int) = "shared_" + Ashes.Text.fromInt(n)

let run (seed: Int) =
    match build2465(seed)([label2465(seed + 0), label2465(seed + 1), label2465(seed + 2)])([seed]) with
        | (cx, _other) -> size2465(cx.labelBlocks)(0)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + run(n))

0
|> loop(2)
|> Ashes.Text.fromInt
|> Ashes.IO.print
