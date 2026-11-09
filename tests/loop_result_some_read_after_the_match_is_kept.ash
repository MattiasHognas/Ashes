// expect: 191
let recursive lookupNext (label: Str) (labels: List((Str, Str))) =
    match labels with
        | [] -> None
        | (candidate, next) :: rest ->
            if candidate == label
            then Some(next)
            else lookupNext(label)(rest)

let recursive innermostStage (hops: Int) (label: Str) (labels: List((Str, Str))) =
    if hops <= 0
    then Some(label)
    else
        match lookupNext(label)(labels) with
            | Some(next) -> innermostStage(hops - 1)(next)(labels)
            | None -> None

let stageName (labels: List((Str, Str))) (start: Str) =
    (let found =
        match innermostStage(0)(start)(labels) with
            | Some(stage) -> stage
            | None -> ""
    in Ashes.Text.length(found + "!"))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + stageName([])("lambda_" + Ashes.Text.fromInt(n)))

0
|> loop(20)
|> Ashes.IO.print
