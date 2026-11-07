// expect: 255
// A fresh list of strings is handed to a closure the caller cannot name, whose arena result holds the
// strings in tuples; the list is released with its parts once the record holding the result has its own.
type Kind =
    | Label(Str)
    | Other(Int)

type Instr =
    | instruction: Kind
    | line: Int

type Context =
    | width: Int
    | labelBlocks: List((Str, Int))

let recursive collectLabelNames (instructions: List(Instr)) =
    match instructions with
        | [] -> []
        | instruction :: rest ->
            match instruction with
                | Instr { instruction = kind } ->
                    match kind with
                        | Label(name) -> name :: collectLabelNames(rest)
                        | _ -> collectLabelNames(rest)

let recursive createLabelBlocks (base: Int) (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest -> (name, base) :: createLabelBlocks(base + 1)(rest)

let buildContext (width: Int) consume (instructions: List(Instr)) =
    (let labelBlocks =
        instructions
        |> collectLabelNames
        |> consume
    in
        let cx = Context(width = width, labelBlocks = labelBlocks)
        in (cx, instructions))

let recursive size (entries: List((Str, Int))) (n: Int) =
    match entries with
        | [] -> n
        | (_name, b) :: rest -> size(rest)(n + b)

let recursive ops (seed: Int) (k: Int) (acc: List(Instr)) =
    if k == 0
    then acc
    else ops(seed)(k - 1)(Instr(instruction = Label("end_" + Ashes.Text.fromInt(seed + k)), line = k) :: Instr(instruction = Other(k), line = k) :: acc)

let emitFunction (seed: Int) =
    match []
    |> ops(seed)(3)
    |> buildContext(seed)(createLabelBlocks(seed)) with
        | (cx, instructions) -> size(cx.labelBlocks)(0) + Ashes.Collection.List.length(instructions)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + emitFunction(n))

0
|> loop(10)
|> Ashes.Text.fromInt
|> Ashes.IO.print
