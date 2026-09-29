// expect: 1512963|15,12,9,6,3|15!
let recursive startThenGrow (acc: List(Str)) (ps: List(Str)) =
    match ps with
        | [] -> acc
        | first :: rest ->
            match acc with
                | [] -> startThenGrow(first :: [])(rest)
                | _ -> startThenGrow(first :: acc)(rest)

let recursive interleave (acc: List(Str)) (ps: List(Str)) =
    match ps with
        | [] -> acc
        | first :: rest ->
            match acc with
                | [] -> interleave(first :: [])(rest)
                | _ -> interleave(first :: "," :: acc)(rest)

let recursive restartOnLong (acc: List(Str)) (ps: List(Str)) =
    match ps with
        | [] -> acc
        | first :: rest ->
            if Ashes.Text.byteLength(first) > 1
            then restartOnLong(first :: "!" :: [])(rest)
            else restartOnLong(first :: acc)(rest)

let recursive collect (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else collect(n - 1)(Ashes.Text.fromInt(n * 3) :: acc)

let parts = collect(5)([])

Ashes.IO.print(Ashes.Text.join("")(startThenGrow([])(parts)) + "|" + Ashes.Text.join("")(interleave([])(parts)) + "|" + Ashes.Text.join("")(restartOnLong([])(parts)))
