// expect: d4/x,c/x|de!,ab!
let recursive walkNames (queue: List(Str)) (seen: List(Str)) (n: Int) =
    match queue with
        | [] -> Ashes.Text.join(",")(seen)
        | name :: rest ->
            if n > 1
            then walkNames(rest)(Ashes.Text.join("/")([name, "x"]) :: seen)(n + 1)
            else walkNames(rest)(seen)(n + 1)

let recursive keepLong (queue: List(Str)) (kept: List(Str)) =
    match queue with
        | [] -> Ashes.Text.join(",")(kept)
        | name :: rest ->
            if Ashes.Text.byteLength(name) > 1
            then keepLong(rest)(Ashes.Text.join("")([name, "!"]) :: kept)
            else keepLong(rest)(kept)

Ashes.IO.print(walkNames(["a", "b", "c", "d" + Ashes.Text.fromInt(4)])([])(0) + "|" + keepLong(["ab", "c", "de"])([]))
