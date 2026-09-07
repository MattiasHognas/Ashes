// expect: 400000
// generic_append_map_churn_plateau with the record builder's parameter left un-annotated. The
// decision that the parameter's entry normalization makes its read a fresh owned child of the
// record is made before the body is lowered, when an un-annotated parameter's type was still a
// variable: the record stayed in the arena and the owned copy added after the body was orphaned
// in it (20.5 MB at 200000 iterations against 8.2 MB at 20000). The parameter's type is now
// seeded from the record field it is stored into before the body is lowered.
import Ashes.Collection.List as list
type Item =
    | name: Str
    | flag: Bool

let toEntry n = Item(name = n, flag = true)

let recursive loop i acc =
    if i == 0
    then acc
    else
        let entries =
            ["Testing"]
            |> list.map(toEntry)
            |> list.append(list.map(toEntry)([Ashes.Text.fromInt(i)]))
        in loop(i - 1)(acc + list.length(entries))

0
|> loop(200000)
|> Ashes.Text.fromInt
|> Ashes.IO.print
