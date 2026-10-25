let recursive plainViews (buffer: Bytes) (index: Int) (acc: List(Bytes)) =
    if index >= 8
    then acc
    else
        plainViews(buffer)(index + 1)(Ashes.Byte.allocate(2) :: Ashes.Byte.fromText(Ashes.Byte.subView(buffer)(index)(1)) :: acc)

let recursive measure (pieces: List(Bytes)) (sum: Int) =
    match pieces with
        | [] -> sum
        | piece :: rest -> measure(rest)(sum + Ashes.Byte.length(piece))

let recursive repeat (left: Int) (sum: Int) =
    if left <= 0
    then sum
    else
        let buffer = Ashes.Byte.allocate(16)
        in
            repeat(left - 1)(sum + measure(plainViews(buffer)(0)([]))(0))

0
|> repeat(ROUNDS)
|> Ashes.IO.print
