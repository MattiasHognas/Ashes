// expect: noise 5000 | 3 -> item 3 | 2 -> item 2 | 1 -> item 1
// Reverses a list of records through the generic Ashes.Collection.List.reverse and reads the
// result back after 5000 unrelated allocations, exercising the retain of elements a generic
// list-building function moves out of a consumed list into the cells it builds.

import Ashes.Collection.List as list
import Ashes.Text as text
type Item =
    | payload: Str
    | position: Int

let recursive build count acc =
    if count == 0
    then acc
    else build(count - 1)(Item(payload = "item " + text.fromInt(count), position = count) :: acc)

let recursive show items acc =
    match items with
        | [] -> acc
        | Item { payload = payload, position = position } :: rest -> show(rest)(acc + " | " + text.fromInt(position) + " -> " + payload)

let recursive countList items count =
    match items with
        | [] -> count
        | _ :: rest -> countList(rest)(count + 1)

let recursive churn count acc =
    if count == 0
    then acc
    else churn(count - 1)("churn " + text.fromInt(count) :: acc)

let items = build(3)([])

let reversed = list.reverse(items)

let noise = churn(5000)([])

Ashes.IO.print("noise " + text.fromInt(countList(noise)(0)) + show(reversed)(""))
