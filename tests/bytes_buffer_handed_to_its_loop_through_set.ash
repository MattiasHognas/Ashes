// expect: 3780
// A loop hands a buffer to itself through Byte.set: the buffer parameter is reference-counted, and
// each back edge releases the buffer its successor replaces.
let recursive fill (buffer: Bytes) (i: Int) =
    if i >= Ashes.Byte.length(buffer)
    then buffer
    else
        fill(i
        |> Ashes.Number.UInt.fromInt
        |> Ashes.Byte.set(buffer)(i))(i + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let filled =
            fill(Ashes.Byte.allocate(64))(0)
        in
            rounds(n - 1)(total + Ashes.Number.UInt.toInt(Ashes.Byte.get(filled)(63)))

0
|> rounds(60)
|> Ashes.IO.print
