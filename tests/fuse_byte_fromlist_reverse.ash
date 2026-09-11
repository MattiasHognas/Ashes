// Ashes.Byte.fromList(Ashes.Collection.List.reverse(xs)) is fused into a single pass that fills the
// buffer back-to-front while walking xs forward, so the reversed list is never actually built.
// Recognized by resolved callee identity through a whole-module import alias; the fully-qualified
// spelling of the same reference is covered separately at the IR level
// (FusionTests.Byte_fromList_of_List_reverse_lowers_to_a_reversed_single_pass_fill).
// expect: |1,|5,4,3,2,1,|
import Ashes.Collection.List as list
import Ashes.Number.UInt as uint
let recursive show index bytes acc =
    if index == Ashes.Byte.length(bytes)
    then acc
    else
        show(index + 1)(bytes)(acc + Ashes.Text.fromInt(index
        |> Ashes.Byte.get(bytes)
        |> uint.toInt) + ",")

let showBytes bytes = show(0)(bytes)("")

let empty =
    []
    |> list.reverse
    |> Ashes.Byte.fromList

let single =
    [1u8]
    |> list.reverse
    |> Ashes.Byte.fromList

let several =
    [1u8, 2u8, 3u8, 4u8, 5u8]
    |> list.reverse
    |> Ashes.Byte.fromList

Ashes.IO.print(showBytes(empty) + "|" + showBytes(single) + "|" + showBytes(several) + "|")
