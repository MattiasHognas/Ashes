// expect: 1,3,5|12,34,5
let recursive rev (acc: List(Str)) (xs: List(Str)) =
    match xs with
        | [] -> acc
        | head :: tail -> rev(head :: acc)(tail)

let recursive keepOdd (acc: List(Str)) (ps: List(Str)) =
    match ps with
        | [] -> rev([])(acc)
        | first :: rest ->
            match rest with
                | [] -> rev([])(first :: acc)
                | _ :: more -> keepOdd(first :: acc)(more)

let recursive pairwise (acc: List(Str)) (ps: List(Str)) =
    match ps with
        | [] -> Ashes.Collection.List.reverse(acc)
        | first :: rest ->
            match rest with
                | [] -> Ashes.Collection.List.reverse(first :: acc)
                | second :: more -> pairwise(first + second :: acc)(more)

let recursive collect (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

let parts = collect(5)([])

Ashes.IO.print(Ashes.Text.join(",")(keepOdd([])(parts)) + "|" + Ashes.Text.join(",")(pairwise([])(parts)))
