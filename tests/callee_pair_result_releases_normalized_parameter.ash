// A callee that rebuilds a record from its parameter and returns it beside a second result: the
// pair is copied into an owned value and the callee's normalized parameter is released behind
// it, as it already was for a record returned on its own.
// expect: 486600
type Where =
    | line: Int
    | column: Int

type Instr =
    | kind: Int
    | target: Int
    | location: Where

let step (kind: Int) (target: Int) (location: Where) = (target, Instr(kind = kind + 1, target = target, location = location))

let recursive pass (instructions: List(Instr)) (acc: List(Instr)) =
    match instructions with
        | [] -> Ashes.Collection.List.reverse(acc)
        | Instr { kind = kind, target = target, location = location } :: tail ->
            match step(kind)(target)(location) with
                | (_n, rewritten) -> pass(tail)(rewritten :: acc)

let recursive build (n: Int) (acc: List(Instr)) =
    if n == 0
    then acc
    else build(n - 1)(Instr(kind = n - n / 8 * 8, target = n, location = Where(line = n, column = 1)) :: acc)

let recursive weigh (xs: List(Instr)) (total: Int) =
    match xs with
        | [] -> total
        | Instr { kind = kind, target = target, location = location } :: rest -> weigh(rest)(total + kind + target + location.line)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + weigh(pass(build(400)([]))([]))(0))

0
|> rounds(3)
|> Ashes.IO.print
