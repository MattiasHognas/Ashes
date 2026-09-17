// expect: 3
import Ashes.IO as io
type Entry =
    | entryName: Str
    | entryCount: Int

let capped (value: Int) =
    if value >= 2
    then 2
    else value

let recursive kept (entries: List(Entry)) =
    match entries with
        | [] -> []
        | Entry { entryName = name, entryCount = count } :: rest ->
            if count > 100
            then kept(rest)
            else Entry(entryName = name, entryCount = capped(count)) :: kept(rest)

let recursive total (entries: List(Entry)) (acc: Int) =
    match entries with
        | [] -> acc
        | Entry { entryCount = count } :: rest -> total(rest)(acc + count)

let entries = [Entry(entryName = "a" + Ashes.Text.fromInt(1), entryCount = 1), Entry(entryName = "b" + Ashes.Text.fromInt(2), entryCount = 5)]

0
|> total(kept(entries))
|> Ashes.Text.fromInt
|> io.print
