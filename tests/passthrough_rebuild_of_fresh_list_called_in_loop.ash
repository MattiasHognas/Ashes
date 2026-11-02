// expect: 7000
type Rank =
    | Low
    | High

let rank (score: Int) =
    if score > 1
    then High
    else Low

let recursive mark (scores: List(Int)) (marks: List(Maybe(Rank))) =
    match (scores, marks) with
        | (score :: restScores, _current :: restMarks) ->
            Some(rank(score)) :: mark(restScores)(restMarks)
        | _ -> marks

let recursive blank (count: Int) =
    if count == 0
    then []
    else None :: blank(count - 1)

let recursive countHigh (marks: List(Maybe(Rank))) =
    match marks with
        | [] -> 0
        | Some(High) :: rest -> 1 + countHigh(rest)
        | _ :: rest -> countHigh(rest)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + countHigh(3
        |> blank
        |> mark([n % 3, 2, 3])))

0
|> rounds(3000)
|> Ashes.IO.print
