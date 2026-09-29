// expect: 900

let recursive lowerEach (items: List(Int)) =
    match items with
        | [] -> []
        | head :: rest ->
            if head > 1
            then (head - 1) :: lowerEach(rest)
            else head :: lowerEach(rest)

let recursive settle (values: List(Int)) =
    (let next = lowerEach(values)
    in
        if next == values
        then next
        else settle(next))

let recursive sum (items: List(Int)) (total: Int) =
    match items with
        | [] -> total
        | head :: rest -> sum(rest)(total + head)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + sum(settle([2, 3, 4]))(0))

0
|> rounds(300)
|> Ashes.IO.print
