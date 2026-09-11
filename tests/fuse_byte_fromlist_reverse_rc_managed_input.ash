// The reversed list's source can itself be a fresh, reference-counted spine (here, the output of a
// generic List.map, whose element type is still a type variable at its own definition and so builds
// an RC-managed cell per element) — the fusion must retain/release that input exactly as the
// unfused Byte.fromList(reverse(xs)) chain would, with no leak and no double release. List.map's own
// recursion (not TMC-eligible at a still-generic element type, and not what this fusion changes)
// costs real native stack per element, so this stays well below where either compiled program
// faults on an ordinary 8 MiB stack.
// expect: 1884461
import Ashes.Collection.List as list
import Ashes.Number.UInt as uint
let recursive build count acc =
    if count == 0
    then acc
    else build(count - 1)(uint.fromInt(count % 251) :: acc)

let increment value = uint.fromInt(uint.toInt(value) + 1)

let recursive sum index bytes acc =
    if index == Ashes.Byte.length(bytes)
    then acc
    else
        sum(index + 1)(bytes)(acc + uint.toInt(Ashes.Byte.get(bytes)(index)))

let mapped =
    []
    |> build(15000)
    |> list.map(increment)

let bytes =
    mapped
    |> list.reverse
    |> Ashes.Byte.fromList

0
|> sum(0)(bytes)
|> Ashes.Text.fromInt
|> Ashes.IO.print
