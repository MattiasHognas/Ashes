// A tail-modulo-cons rewrite returning a borrowed list parameter in its base case, over records the
// ownership contract covers: the base case holds a reference of its own, as the last cell's tail does.
// expect: 75
import Ashes.IO as io
type Entry =
    | name: Str
    | count: Int

let recursive countOf (entries: List(Entry)) (wanted: Str) =
    match entries with
        | [] -> -1
        | Entry { name = name, count = count } :: rest ->
            if name == wanted
            then count
            else countOf(rest)(wanted)

let recursive maximizedOntoLeft (left: List(Entry)) (right: List(Entry)) (added: List(Entry)) =
    match left with
        | [] -> added
        | (Entry { name = name, count = count } as entry) :: rest ->
            let other = countOf(right)(name)
            in
                if other < 0 || count > other
                then entry :: maximizedOntoLeft(rest)(right)(added)
                else Entry(name = name, count = other) :: maximizedOntoLeft(rest)(right)(added)

let recursive absentFromLeft (right: List(Entry)) (left: List(Entry)) =
    match right with
        | [] -> []
        | (Entry { name = name } as entry) :: rest ->
            if countOf(left)(name) >= 0
            then absentFromLeft(rest)(left)
            else entry :: absentFromLeft(rest)(left)

let maxCounts (left: List(Entry)) (right: List(Entry)) =
    left
    |> absentFromLeft(right)
    |> maximizedOntoLeft(left)(right)

let recursive entriesFor (index: Int) (count: Int) (acc: List(Entry)) =
    if count <= 0
    then acc
    else entriesFor(index + 3)(count - 1)(Entry(name = "p" + Ashes.Text.fromInt(index % 17), count = index % 3) :: acc)

let recursive total (entries: List(Entry)) (sum: Int) =
    match entries with
        | [] -> sum
        | Entry { name = name, count = count } :: rest -> total(rest)(sum + count + Ashes.Text.byteLength(name))

let recursive rounds (round: Int) (acc: List(Entry)) =
    if round >= 400
    then total(acc)(0)
    else
        []
        |> entriesFor(round)(5)
        |> maxCounts(acc)
        |> rounds(round + 1)

[]
|> rounds(0)
|> io.print
