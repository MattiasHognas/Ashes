// expect: noise 5000 | Mid yes | Testing no | Base yes
// Appends two lists a generic Ashes.Collection.List.map built from records, then reads the result
// back after 5000 unrelated allocations. Each map result is a fresh runtime-managed deep copy the
// caller owns and consumes into the generic append, whose own arena cells reference those records
// until the caller deep-copies the appended result; the consumed arguments must be released only
// after that copy, not before it.

import Ashes.Collection.List as list
import Ashes.Text as text
type Entry =
    | name: Str
    | direct: Bool

let toEntry (direct: Bool) (name: Str) = Entry(name = name, direct = direct)

let recursive show entries acc =
    match entries with
        | [] -> acc
        | Entry { name = name, direct = direct } :: rest ->
            show(rest)(acc + " | " + name + (if direct then " yes" else " no"))

let recursive countList items count =
    match items with
        | [] -> count
        | _ :: rest -> countList(rest)(count + 1)

let recursive churn count acc =
    if count == 0
    then acc
    else churn(count - 1)("churn " + text.fromInt(count) :: acc)

let entries =
    list.append(list.append(list.map(toEntry(true))(["Mid"]))(list.map(toEntry(false))(["Testing"])))(list.map(toEntry(true))(["Base"]))

let noise = churn(5000)([])

Ashes.IO.print("noise " + text.fromInt(countList(noise)(0)) + show(entries)(""))
