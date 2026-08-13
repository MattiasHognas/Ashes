// expect: 12520

import Ashes.Text.Source
import Ashes.Text.Source.Position
let utf16 = Ashes.Text.Source.byteOffsetToPosition("é\r\n😀é")(Ashes.Text.Source.Utf16)(8)

let scalar = Ashes.Text.Source.byteOffsetToPosition("é\r\n😀é")(Ashes.Text.Source.UnicodeScalar)(8)

let reverse = Ashes.Text.Source.positionToByteOffset("a😀b")(Ashes.Text.Source.Utf16)(Position(line = 0, character = 3))

let endBoundary =
    if Ashes.Text.Source.isBoundary("😀")(4)
    then 10
    else 0

let middleBoundary =
    if Ashes.Text.Source.isBoundary("😀")(2)
    then 1
    else 0

Ashes.IO.print(utf16.line * 10000 + utf16.character * 1000 + reverse * 100 + scalar.character * 10 + endBoundary + middleBoundary)
