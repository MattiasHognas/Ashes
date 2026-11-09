// expect: 160
type Resolution =
    | Known(Str)
    | Pending

let wrap (label: Str) (flag: Bool) = Known(label)

let recursive group (labels: List(Str)) (agreed: Maybe(Str)) =
    match labels with
        | [] ->
            match agreed with
                | Some(found) -> Ashes.Text.length(found)
                | None -> 0
        | raw :: tail ->
            match wrap(raw)(false) with
                | Pending -> group(tail)(agreed)
                | Known(label) -> group(tail)(Some(label))

let probe (n: Int) =
    (let label = "lambda_" + Ashes.Text.fromInt(n % 10)
    in group([label, label])(None))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(20)
|> Ashes.IO.print
