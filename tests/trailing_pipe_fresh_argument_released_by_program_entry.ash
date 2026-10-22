// expect: 44

type Where =
    | line: Int
    | column: Int

type Instr =
    | kind: Int
    | location: Where

let step (kind: Int) (location: Where) = (kind, Instr(kind = kind + 1, location = location))

let recursive pass (instructions: List(Instr)) (total: Int) =
    match instructions with
        | [] -> total
        | Instr { kind = kind, location = location } :: tail ->
            match step(kind)(location) with
                | (_n, rewritten) -> pass(tail)(total + rewritten.kind)

let recursive build (n: Int) (acc: List(Instr)) =
    if n == 0
    then acc
    else build(n - 1)(Instr(kind = n, location = Where(line = n, column = 1)) :: acc)

0
|> pass(build(8)([]))
|> Ashes.IO.print
