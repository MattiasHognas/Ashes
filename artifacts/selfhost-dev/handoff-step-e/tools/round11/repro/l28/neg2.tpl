let keep (xs: List(Int)) = Some(xs)

let recursive pick (values: List(Int)) (kept: Maybe(List(Int))) =
    match values with
        | [] -> kept
        | _value :: rest -> other(rest)(keep(values))
and other (values: List(Int)) (kept: Maybe(List(Int))) = pick(values)(kept)

let recursive total (k: Int) (xs: List(Int)) =
    match xs with
        | [] -> k
        | a :: rest -> total(k + a)(rest)

let probe (n: Int) =
    match pick([n, 7, n % 5])(None) with
        | Some(xs) -> total(0)(xs)
        | None -> 0

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))
in Ashes.IO.print(Ashes.Text.fromInt(loop(ROUNDS)(0)))
