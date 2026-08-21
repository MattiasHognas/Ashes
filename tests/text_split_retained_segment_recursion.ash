// expect: ok

import Ashes.Text
import Ashes.Byte
let sameText left right = Ashes.Byte.compare(Ashes.Byte.fromText(left))(Ashes.Byte.fromText(right)) == 0

let recursive containsText name names =
    match names with
        | [] -> false
        | head :: tail ->
            if sameText(name)(head)
            then true
            else containsText(name)(tail)

let recursive lastPart parts =
    match parts with
        | [] -> ""
        | name :: [] -> name
        | _head :: tail -> lastPart(tail)

let leaf name = lastPart(Ashes.Text.split(name)("."))

let recursive check remaining seen =
    match remaining with
        | [] -> "ok"
        | written :: tail ->
            let current = leaf(written)
            in
                if containsText(current)(seen)
                then "duplicate " + current
                else check(tail)(current :: seen)

Ashes.IO.print(check(["Eq", "Ord", "Show", "Hash"])([]))
