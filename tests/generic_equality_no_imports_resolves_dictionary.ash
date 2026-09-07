// expect: b
let recursive firstMatch key entries =
    match entries with
        | [] -> "none"
        | (k, v) :: tail ->
            if k == key
            then v
            else firstMatch(key)(tail)

[("x", "a"), ("y", "b"), ("z", "c")]
|> firstMatch("y")
|> Ashes.IO.print
