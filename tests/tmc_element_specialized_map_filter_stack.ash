// The stack-depth claim for the shipped generic producers: a List.map or List.filter call whose
// callback fixes the element type runs an element-specialized copy, whose tail-modulo-constructor spine
// costs no native stack. Each list here is several times longer than the roughly 170000 elements the
// generic recursion survives on an ordinary 8 MiB stack. The string map projects a field off records the
// map consumes, so its cells retain heads borrowed from a list released as the traversal proceeds.
// expect: 500001500000 250000 3400000
import Ashes.Collection.List as list
import Ashes.IO as io
import Ashes.Text as text
type Row =
    | label: Str
    | weight: Int

let recursive build count acc =
    if count == 0
    then acc
    else build(count - 1)(count :: acc)

let recursive rows count acc =
    if count == 0
    then acc
    else rows(count - 1)(Row(label = "r" + text.fromInt(count % 10), weight = count) :: acc)

let increment (value: Int) = value + 1

let isQuarter (value: Int) = value % 4 == 0

let labelOf (row: Row) = row.label

let add (acc: Int) (value: Int) = acc + value

let addLength (acc: Int) (value: Str) = acc + Ashes.Text.byteLength(value)

let mapped =
    []
    |> build(1000000)
    |> list.map(increment)

let filtered =
    []
    |> build(1000000)
    |> list.filter(isQuarter)

let labels =
    []
    |> rows(1700000)
    |> list.map(labelOf)

text.fromInt(list.foldLeft(add)(0)(mapped)) + " " + text.fromInt(list.length(filtered)) + " " + text.fromInt(list.foldLeft(addLength)(0)(labels)) |> io.print
