// expect: 9
let recursive enqueue (pending: List(Str)) (back: List(Str)) (index: Int) =
    match pending with
        | [] -> back
        | key :: rest ->
            if index == 1
            then enqueue(rest)(back)(index + 1)
            else enqueue(rest)(key :: back)(index + 1)

let recursive countAll (values: List(Str)) (total: Int) =
    match values with
        | [] -> total
        | _value :: rest -> countAll(rest)(total + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let keys = ["a" + "1", "b" + "2", "c" + "3", "d" + "4"]
        in
            rounds(n - 1)(total + countAll(enqueue(keys)([])(0))(0))

0
|> rounds(3)
|> Ashes.IO.print
