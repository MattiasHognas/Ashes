// The fusion fires through an unaliased selector import too — resolved callee identity, not the
// literal spelling `reverse`, is what recognizes Ashes.Collection.List.reverse.
// expect: 9,7,5,3,1,
import Ashes.Collection.List.reverse
import Ashes.Number.UInt as uint
let recursive show index bytes acc =
    if index == Ashes.Byte.length(bytes)
    then acc
    else
        show(index + 1)(bytes)(acc + Ashes.Text.fromInt(index
        |> Ashes.Byte.get(bytes)
        |> uint.toInt) + ",")

let bytes =
    [1u8, 3u8, 5u8, 7u8, 9u8]
    |> reverse
    |> Ashes.Byte.fromList

""
|> show(0)(bytes)
|> Ashes.IO.print
