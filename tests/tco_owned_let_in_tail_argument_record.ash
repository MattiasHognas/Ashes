// expect: 1;2;3;
type Inst =
    | Jump(Str)
    | Other

type Wrapped =
    | instruction: Inst
    | location: Maybe(Int)

let mk n = Ashes.Text.fromInt(n)

let recursive loop n acc =
    if n == 0
    then acc
    else
        let label = mk(n)
        in loop(n - 1)(Wrapped(instruction = Jump(label), location = None) :: acc)

let recursive render items =
    match items with
        | [] -> ""
        | Wrapped { instruction = Jump(label), location = _ } :: rest -> label + ";" + render(rest)
        | _ :: rest -> "other;" + render(rest)

Ashes.IO.print(render(loop(3)([])))
