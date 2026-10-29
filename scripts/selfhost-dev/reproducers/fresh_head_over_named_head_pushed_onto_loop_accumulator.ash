let recursive build (items: List(Bytes)) (acc: List(Bytes)) =
    match items with
        | [] -> acc
        | item :: rest -> build(rest)(Ashes.Byte.allocate(3) :: item :: acc)

let recursive count (xs: List(Bytes)) (total: Int) =
    match xs with
        | [] -> total
        | x :: rest -> count(rest)(total + Ashes.Byte.length(x))

let source = [Ashes.Byte.allocate(1), Ashes.Byte.allocate(2), Ashes.Byte.allocate(3), Ashes.Byte.allocate(4), Ashes.Byte.allocate(5)]

let recursive repeat (left: Int) (total: Int) =
    if left <= 0
    then total
    else repeat(left - 1)(total + count(build(source)([]))(0))

repeat(ROUNDS)(0) |> Ashes.IO.print
