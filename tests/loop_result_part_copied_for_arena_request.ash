// expect: 8
type Kind =
    | Call(Int, Str, Int)
    | Other(Int)

type Instruction =
    | kind: Kind
    | note: Maybe(Str)

let recursive setAssociation key value entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setAssociation(key)(value)(tail)

let recursive collect (remaining: List((Int, Instruction))) (count: Int) (expansions: List((Int, List(Instruction)))) rewrites =
    match remaining with
        | [] -> (expansions, rewrites, count)
        | (position, current) :: tail ->
            match current with
                | Instruction { kind = Call(_, _, env), note = note } ->
                    collect(tail)(count + 1)(setAssociation(position)([Instruction(kind = Other(env), note = note)])(expansions))(rewrites)
                | _ -> collect(tail)(count)(expansions)(rewrites)

let recursive index (items: List(Instruction)) (n: Int) acc =
    match items with
        | [] -> Ashes.Collection.List.reverse(acc)
        | item :: rest -> index(rest)(n + 1)((n, item) :: acc)

let probe (seed: Int) =
    (let items = [Instruction(kind = Call(1)("ash" + Ashes.Text.fromInt(seed))(2), note = Some("a" + Ashes.Text.fromInt(seed))), Instruction(kind = Call(5)("t")(6), note = None), Instruction(kind = Other(seed), note = None)]
    in
        match collect(index(items)(0)([]))(0)([])([]) with
            | (expansions, _rewrites, count) -> Ashes.Collection.List.length(expansions) + count)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(2)
|> Ashes.IO.print
