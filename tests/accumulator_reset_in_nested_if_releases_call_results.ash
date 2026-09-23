// expect: 2156000
// A loop accumulator that one arm conses a let-bound call result onto, another hands on and a
// third resets to nil, inside a nested if, lives on the reference-counted heap and releases every
// element. Nil was not an accumulator arm, so the list stayed in the arena holding retained
// elements; and the nested join, normalized in place, was retained again by the outer one.
type Diag =
    | message: Int
    | at: Int

let readNext (position: Int) = Diag(message = position * 3, at = position)

let recursive scan (limit: Int) (position: Int) (diagnostics: List(Diag)) =
    if position >= limit
    then diagnostics
    else
        match readNext(position) with
            | diagnostic ->
                scan(limit)(position + 1)(if position - position / 7 * 7 == 0
                then diagnostic :: diagnostics
                else
                    if position == 50
                    then []
                    else diagnostics)

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
