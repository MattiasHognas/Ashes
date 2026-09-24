// expect: 500500
// A record parameter an outer curried stage keeps in the closure it returns is captured, not copied
// at entry: the entry copy lived in an arena closure environment that was never released, so every
// call leaked one record.
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
|> rounds(1000)
|> Ashes.IO.print
