// expect: 600
// A loop over instructions keeps two association tables: the ints known at the current point and
// the ints saved at each jump target. A label takes the saved table out of a lookup's result, and a
// constant or a jump replaces a table with a helper's owned result. Both tables are placed on the
// reference-counted heap, so the back edge releases each predecessor and no table survives the loop.
import Ashes.Collection.List.reverse
import Ashes.Collection.List.length
import Ashes.Text.fromInt
type Inst =
    | ConstInt(Int, Int)
    | Jump(Str)
    | JumpIfFalse(Int, Str)
    | Label(Str)
    | Other(Int)

let recursive lookupAssociation (key: Str) (entries: List((Str, List((Int, Int))))) =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupAssociation(key)(tail)

let recursive setInt (key: Int) (value: Int) entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setInt(key)(value)(tail)

let recursive setSaved (key: Str) (value: List((Int, Int))) entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setSaved(key)(value)(tail)

let recursive removeAssociation (key: Str) (entries: List((Str, List((Int, Int))))) =
    match entries with
        | [] -> []
        | (k, v) :: tail ->
            if k == key
            then tail
            else (k, v) :: removeAssociation(key)(tail)

let reduce instructions =
    (let recursive reduceLoop insts knownInts savedInts prevTerm acc =
        match insts with
            | [] -> reverse(acc)
            | inst :: tail ->
                match inst with
                    | ConstInt(dest, v) ->
                        reduceLoop(tail)(setInt(dest)(v)(knownInts))(savedInts)(false)(inst :: acc)
                    | Label(name) ->
                        let nextInts =
                            if prevTerm
                            then
                                match lookupAssociation(name)(savedInts) with
                                    | Some(si) -> si
                                    | None -> []
                            else knownInts
                        in
                            reduceLoop(tail)(nextInts)(removeAssociation(name)(savedInts))(false)(inst :: acc)
                    | Jump(target) ->
                        let nextSaved = setSaved(target)(knownInts)(savedInts)
                        in reduceLoop(tail)(knownInts)(nextSaved)(true)(inst :: acc)
                    | JumpIfFalse(_, target) ->
                        let nextSaved = setSaved(target)(knownInts)(savedInts)
                        in reduceLoop(tail)(knownInts)(nextSaved)(false)(inst :: acc)
                    | _ -> reduceLoop(tail)(knownInts)(savedInts)(false)(inst :: acc)
    in reduceLoop(instructions)([])([])(false)([]))

let recursive build i acc =
    if i == 0
    then acc
    else
        let l = "L" + fromInt(i % 7)
        in build(i - 1)(ConstInt(i)(i * 2) :: JumpIfFalse(i)(l) :: Other(i) :: Jump(l) :: Label(l) :: acc)

let recursive rounds n total =
    if n == 0
    then total
    else
        rounds(n - 1)(total + length([]
        |> build(40)
        |> reduce))

0
|> rounds(3)
|> fromInt
|> Ashes.IO.print
