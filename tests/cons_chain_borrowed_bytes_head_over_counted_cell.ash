// expect: 360 1800
let recursive sources (n: Int) (acc: List(Bytes)) =
    if n <= 0
    then acc
    else sources(n - 1)(Ashes.Byte.allocate(n) :: acc)

let recursive matchedHeads (items: List(Bytes)) (acc: List(Bytes)) =
    match items with
        | [] -> Ashes.Collection.List.reverse(acc)
        | payload :: rest -> matchedHeads(rest)(Ashes.Byte.allocate(2) :: payload :: Ashes.Byte.allocate(3) :: acc)

let recursive markerHeads (marker: Bytes) (items: List(Bytes)) (acc: List(Bytes)) =
    match items with
        | [] -> Ashes.Collection.List.reverse(acc)
        | payload :: rest ->
            markerHeads(marker)(rest)(Ashes.Byte.allocate(Ashes.Byte.length(payload)) :: marker :: Ashes.Byte.allocate(3) :: acc)

let recursive totalLength (pieces: List(Bytes)) (total: Int) =
    match pieces with
        | [] -> total
        | piece :: rest -> totalLength(rest)(total + Ashes.Byte.length(piece))

let recursive repeat (k: Int) (count: Int) (bytes: Int) =
    if k == 0
    then Ashes.Text.fromInt(count) + " " + Ashes.Text.fromInt(bytes)
    else
        let matched =
            matchedHeads(sources(20)([]))([])
        in
            let marked =
                markerHeads(Ashes.Byte.allocate(1))(sources(20)([]))([])
            in repeat(k - 1)(count + Ashes.Collection.List.length(matched) + Ashes.Collection.List.length(marked))(bytes + totalLength(matched)(0) + totalLength(marked)(0))

0
|> repeat(3)(0)
|> Ashes.IO.print
