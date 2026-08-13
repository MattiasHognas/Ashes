// expect: empty=|text=A😀å
import Ashes.Text as text
let empty = text.fromRunes([])

let value = text.fromRunes('A' :: '😀' :: 'å' :: [])

Ashes.IO.print("empty=" + empty + "|text=" + value)
