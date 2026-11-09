// expect: 120
// A loop keeps its known table in the arena: it conses onto the table, and at a label takes the
// table saved for it out of a guarded lookup's result. The join of that part with the table would
// have been copied onto the reference-counted heap, and the arena parameter never releases what it
// is given; the lookup's part is copied into the arena instead and its own reference released.
import Ashes.Collection.List.length
import Ashes.Text.fromInt
type Inst =
    | ConstInt(Int, Int)
    | Label(Str)

let recursive lookupSaved (key: Str) (entries: List((Str, List((Int, Int))))) =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupSaved(key)(tail)

let recursive reduceLoop insts knownInts savedInts prevTerm =
    match insts with
        | [] -> length(knownInts)
        | inst :: tail ->
            match inst with
                | ConstInt(dest, v) -> reduceLoop(tail)((dest, v) :: knownInts)(savedInts)(false)
                | Label(name) ->
                    let nextInts =
                        if prevTerm
                        then
                            match lookupSaved(name)(savedInts) with
                                | Some(si) when length(si) >= 0 -> si
                                | _ -> []
                        else knownInts
                    in reduceLoop(tail)(nextInts)(savedInts)(false)

let recursive build i acc =
    if i == 0
    then acc
    else build(i - 1)(ConstInt(i)(i * 2) :: Label("L") :: acc)

let recursive rounds n total =
    if n == 0
    then total
    else
        rounds(n - 1)(total + reduceLoop(build(40)([]))([])([])(false))

0
|> rounds(3)
|> fromInt
|> Ashes.IO.print
