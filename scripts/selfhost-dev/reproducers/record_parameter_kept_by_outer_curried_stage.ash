type Where =
    | line: Int
    | column: Int

type Instr =
    | instruction: Int
    | location: Where

let make (loc: Where) (k: Int) = Instr(instruction = k, location = loc)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match make(Where(line = n, column = 1))(n) with
            | Instr { location = w } -> rounds(n - 1)(total + w.line)

0
|> rounds(ROUNDS)
|> Ashes.IO.print
