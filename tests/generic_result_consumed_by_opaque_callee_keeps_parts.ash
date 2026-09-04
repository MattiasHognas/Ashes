// expect: noise 5000 | Mid | Testing
// Two lists a generic map built from records are appended by a generic append, and the appended
// result — a fresh runtime-managed deep copy the caller owns — is consumed by a helper whose
// result the ownership analysis cannot see through (it hands the record's string to an unknown
// closure that stores it into the state it returns, so the helper's result reach is unknown).
// The stored strings are read back after 5000 unrelated allocations: the caller must not release
// the consumed list's records while that un-normalized result may still borrow their strings.

import Ashes.Collection.List as list
import Ashes.Text as text
type Entry =
    | name: Str
    | direct: Bool

type Labels =
    | seen: List(Str)

let toEntry (direct: Bool) (name: Str) = Entry(name = name, direct = direct)

let record (label: Str) (state: Labels) = Labels(seen = label :: state.seen)

let recursive stash (entries: List(Entry)) (store: Str -> Labels -> Labels) (state: Labels) =
    match entries with
        | [] -> state
        | Entry { name = name } :: rest -> stash(rest)(store)(store(name)(state))

let recursive show labels acc =
    match labels with
        | [] -> acc
        | label :: rest -> show(rest)(acc + " | " + label)

let recursive countList items count =
    match items with
        | [] -> count
        | _ :: rest -> countList(rest)(count + 1)

let recursive churn count acc =
    if count == 0
    then acc
    else churn(count - 1)("churn " + text.fromInt(count) :: acc)

let labels =
    stash(list.append(list.map(toEntry(true))(["Testing"]))(list.map(toEntry(false))(["Mid"])))(record)(Labels(seen = []))

let noise = churn(5000)([])

Ashes.IO.print("noise " + text.fromInt(countList(noise)(0)) + show(labels.seen)(""))
