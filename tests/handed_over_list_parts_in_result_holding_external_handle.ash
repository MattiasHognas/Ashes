// expect: 30
// A fresh list of strings is handed to a closure the caller cannot name, whose result pairs each
// string with a foreign handle; the record holding the result is a contract value all the same, so the
// list is released with its parts behind it.
external type Handle
external handleOf(Str) -> Handle = "strlen"

type Kind =
    | Label(Str)
    | Other(Int)

type Instr =
    | instruction: Kind
    | line: Int

type Context =
    | width: Int
    | labelBlocks: List((Str, Handle))

let recursive collectLabelNames (instructions: List(Instr)) =
    match instructions with
        | [] -> []
        | instruction :: rest ->
            match instruction with
                | Instr { instruction = kind } ->
                    match kind with
                        | Label(name) -> name :: collectLabelNames(rest)
                        | _ -> collectLabelNames(rest)

let recursive createLabelBlocks (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest -> (name, handleOf(name)) :: createLabelBlocks(rest)

let buildContext (width: Int) consume (instructions: List(Instr)) =
    (let labelBlocks =
        instructions
        |> collectLabelNames
        |> consume
    in
        let cx = Context(width = width, labelBlocks = labelBlocks)
        in (cx, instructions))

let recursive count (entries: List((Str, Handle))) (n: Int) =
    match entries with
        | [] -> n
        | (_name, _handle) :: rest -> count(rest)(n + 1)

let recursive ops (seed: Int) (k: Int) (acc: List(Instr)) =
    if k == 0
    then acc
    else ops(seed)(k - 1)(Instr(instruction = Label("end_" + Ashes.Text.fromInt(seed + k)), line = k) :: Instr(instruction = Other(k), line = k) :: acc)

let emitFunction (seed: Int) =
    match []
    |> ops(seed)(3)
    |> buildContext(seed)(createLabelBlocks) with
        | (cx, _instructions) -> count(cx.labelBlocks)(0)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + emitFunction(n))

0
|> loop(10)
|> Ashes.Text.fromInt
|> Ashes.IO.print
