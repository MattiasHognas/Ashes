// expect: 1500

let recursive replaceAt (items: List(Int)) (index: Int) (replacement: Int) =
    match items with
        | [] -> []
        | head :: rest ->
            if index == 0
            then replacement :: rest
            else head :: replaceAt(rest)(index - 1)(replacement)

let recursive round (order: List(Int)) (idoms: List(Int)) =
    match order with
        | [] -> idoms
        | block :: rest ->
            if block == 0
            then round(rest)(idoms)
            else
                block - 1
                |> replaceAt(idoms)(block)
                |> round(rest)

let recursive sum (items: List(Int)) (total: Int) =
    match items with
        | [] -> total
        | head :: rest -> sum(rest)(total + head)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + sum(round([0, 1, 2, 3, 4])([-1, -1, -1, -1, n]))(0))

0
|> rounds(300)
|> Ashes.IO.print
