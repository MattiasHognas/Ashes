// expect: 865
let recursive lookupReturned (label: Str) (labels: List((Str, Str))) =
    match labels with
        | [] -> None
        | (candidate, returned) :: rest ->
            if candidate == label
            then Some(returned)
            else lookupReturned(label)(rest)

let recursive innermostStage (hops: Int) (label: Str) (labels: List((Str, Str))) =
    if hops <= 0
    then Some(label)
    else
        match lookupReturned(label)(labels) with
            | Some(next) -> innermostStage(hops - 1)(next)(labels)
            | None -> None

let stageLength (labels: List((Str, Str))) (start: Str) (hops: Int) =
    match innermostStage(hops)(start)(labels) with
        | Some(stage) -> Ashes.Text.length(stage)
        | None -> 0

let recursive makeLabels (n: Int) (acc: List((Str, Str))) =
    if n == 0
    then acc
    else makeLabels(n - 1)(("lambda_" + Ashes.Text.fromInt(n), "lambda_" + Ashes.Text.fromInt(n + 1)) :: acc)

let recursive loop (n: Int) (labels: List((Str, Str))) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(labels)(acc + stageLength(labels)("lambda_" + Ashes.Text.fromInt(n % 20 + 1))(2))

0
|> loop(100)(makeLabels(30)([]))
|> Ashes.IO.print
