// A user's own `reverse`, unrelated to Ashes.Collection.List.reverse, must run as written — the
// fusion is gated on resolved callee identity, not on the literal name `reverse`. This one inserts
// an extra 9u8 between every element as it walks, so a wrongly fused call would silently drop that
// behavior along with the source list's own true order.
// expect: 3,9,2,9,1,9,
let recursive reverse xs acc =
    match xs with
        | [] -> acc
        | head :: tail -> reverse(tail)(head :: 9u8 :: acc)

let recursive show index bytes acc =
    if index == Ashes.Byte.length(bytes)
    then acc
    else
        show(index + 1)(bytes)(acc + Ashes.Text.fromInt(index
        |> Ashes.Byte.get(bytes)
        |> Ashes.Number.UInt.toInt) + ",")

let bytes =
    []
    |> reverse([1u8, 2u8, 3u8])
    |> Ashes.Byte.fromList

""
|> show(0)(bytes)
|> Ashes.IO.print
