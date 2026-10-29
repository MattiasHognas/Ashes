// expect: 6800
type Box =
    | boxed: Bytes

let recursive build (items: List(Bytes)) (acc: List(Bytes)) =
    match items with
        | [] -> acc
        | item :: rest -> build(rest)(Ashes.Byte.allocate(3) :: item :: acc)

let recursive buildBoxes (items: List(Box)) (acc: List(Box)) =
    match items with
        | [] -> acc
        | item :: rest -> buildBoxes(rest)(Box(boxed = Ashes.Byte.allocate(3)) :: item :: acc)

let recursive count (xs: List(Bytes)) (total: Int) =
    match xs with
        | [] -> total
        | x :: rest -> count(rest)(total + Ashes.Byte.length(x))

let source = [Ashes.Byte.allocate(1), Ashes.Byte.allocate(2), Ashes.Byte.allocate(3), Ashes.Byte.allocate(4), Ashes.Byte.allocate(5)]

let boxes = [Box(boxed = Ashes.Byte.allocate(1)), Box(boxed = Ashes.Byte.allocate(2))]

let recursive repeat (left: Int) (total: Int) =
    if left <= 0
    then total
    else
        repeat(left - 1)(total + count(build(source)([]))(0) + Ashes.Collection.List.length(buildBoxes(boxes)([])))

0
|> repeat(200)
|> Ashes.IO.print
