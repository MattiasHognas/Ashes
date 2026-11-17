// expect: kept|2
import Ashes.Collection.List.length
let before left right = left <= right

let effect = Ashes.IO.write("kept|")

[1, 2]
|> length
|> Ashes.Text.fromInt
|> Ashes.IO.print
