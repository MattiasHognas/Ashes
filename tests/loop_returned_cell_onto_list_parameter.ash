// expect: 5,34,12|end,5!,4!,3!,2!,1!|5|12,15,20
let recursive pairwiseGo (acc: List(Str)) (parts: List(Str)) =
    match parts with
        | [] -> acc
        | first :: rest ->
            match rest with
                | [] -> first :: acc
                | second :: more -> pairwiseGo(first + second :: acc)(more)

let recursive stampEnd (acc: List(Str)) (parts: List(Str)) =
    match parts with
        | [] -> "end" :: acc
        | part :: rest -> stampEnd(part + "!" :: acc)(rest)

let recursive keepOdd (acc: List(Str)) (parts: List(Str)) =
    match parts with
        | [] -> acc
        | first :: rest ->
            match rest with
                | [] -> first :: acc
                | _ :: more -> keepOdd(acc)(more)

let recursive sortedSetInsert (item: Int) (set: List(Int)) =
    match set with
        | [] -> [item]
        | head :: rest ->
            if head == item
            then set
            else
                if head > item
                then item :: set
                else head :: sortedSetInsert(item)(rest)

let recursive collect (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

let recursive sumInts (xs: List(Int)) (total: Int) =
    match xs with
        | [] -> total
        | x :: rest -> sumInts(rest)(total + x)

let base =
    [4]
    |> sortedSetInsert(2)
    |> sortedSetInsert(6)

let grown = sortedSetInsert(3)(base)

let parts = collect(5)([])

Ashes.IO.print(Ashes.Text.join(",")(pairwiseGo([])(parts)) + "|" + Ashes.Text.join(",")(stampEnd([])(parts)) + "|" + Ashes.Text.join(",")(keepOdd([])(parts)) + "|" + Ashes.Text.fromInt(sumInts(base)(0)) + "," + Ashes.Text.fromInt(sumInts(grown)(0)) + "," + Ashes.Text.fromInt(sumInts(sortedSetInsert(5)(grown))(0)))
