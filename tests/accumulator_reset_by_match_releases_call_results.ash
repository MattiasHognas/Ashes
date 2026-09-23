// expect: 2156000
// A loop accumulator chosen by a match whose arms cons a call result onto it, reset it to nil, or
// hand it on lives on the reference-counted heap and releases every element, the discarded ones
// included.
type Diag =
    | message: Int
    | at: Int

let readNext (position: Int) = Diag(message = position * 3, at = position)

let choose (position: Int) =
    if position - position / 7 * 7 == 0
    then 0
    else
        if position == 50
        then 1
        else 2

let recursive scan (limit: Int) (position: Int) (diagnostics: List(Diag)) =
    if position >= limit
    then diagnostics
    else
        match readNext(position) with
            | diagnostic ->
                scan(limit)(position + 1)(match choose(position) with
                    | 0 -> diagnostic :: diagnostics
                    | 1 -> []
                    | _ -> diagnostics)

let recursive weigh (xs: List(Diag)) (acc: Int) =
    match xs with
        | [] -> acc
        | diagnostic :: rest -> weigh(rest)(acc + diagnostic.at + diagnostic.message)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + weigh(scan(100)(0)([]))(0))

0
|> rounds(1000)
|> Ashes.IO.print
