// expect: 1714
type Slot =
    | offset: Int
    | index: Int
    | name: Maybe(Str)

let recursive buildSlots captures index =
    match captures with
        | [] -> []
        | _ :: tail ->
            let offset = 160 + index * 8
            in
                let slot = Slot(offset = offset, index = index, name = None)
                in slot :: buildSlots(tail)(index + 1)

let recursive total (xs: List(Slot)) =
    match xs with
        | [] -> 0
        | s :: rest -> s.offset + s.index + total(rest)

let names (n: Int) =
    Ashes.Collection.List.map(given (x: Int) -> "n" + Ashes.Text.fromInt(x))([n, n + 1, n + 2])

let run (seed: Int) =
    (let caps = names(seed)
    in
        total(buildSlots(caps)(0)) + Ashes.Collection.List.length(caps) + total(buildSlots([seed, 2])(1)))

let recursive rounds (n: Int) (sum: Int) =
    if n == 0
    then sum
    else rounds(n - 1)(sum + run(n))

0
|> rounds(2)
|> Ashes.IO.print
