// expect: area=12,9|len=3|tagged=Square:9
import Ashes.Text
import Ashes.Text
import Ashes.IO
type Shape =
    | Circle(Int)
    | Square(Int)

let area s =
    match s with
        | Circle(r) -> 3 * r * r
        | Square(w) -> w * w

type Tagged =
    | Tagged(Str, Int)

let tag s =
    match s with
        | Circle(r) ->
            Circle(r)
            |> area
            |> Tagged("Circle")
        | Square(w) ->
            Square(w)
            |> area
            |> Tagged("Square")

let describe t =
    match t with
        | Tagged(name, value) -> name + ":" + Ashes.Text.fromInt(value)

let areas =
    "area=" + Ashes.Text.fromInt(area(Circle(2))) + "," + Ashes.Text.fromInt(area(Square(3)))

let len =
    "len=" + Ashes.Text.fromInt("  abc  "
    |> Ashes.Text.trim
    |> Ashes.Text.length)

Ashes.IO.print(areas + "|" + len + "|" + "tagged=" + describe(tag(Square(3))))
