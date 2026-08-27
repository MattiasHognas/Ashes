// expect: b
let recursive firstMatch key entries =
    match entries with
        | [] -> "none"
        | (k, v) :: tail ->
            if k == key
            then v
            else firstMatch(key)(tail)

Ashes.IO.print(firstMatch("y")([("x", "a"), ("y", "b"), ("z", "c")]))
