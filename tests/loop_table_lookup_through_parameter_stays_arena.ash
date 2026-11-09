// expect: 600
// The label's lookup is a function the loop receives as a parameter, so the part its result holds is
// not taken as an accumulator edge and the known table stays in the arena. The join copies the part
// into the arena and releases its own reference.
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
    (let recursive reduceLoop (find: Str -> List((Str, List((Int, Int)))) -> Maybe(List((Int, Int)))) insts knownInts savedInts prevTerm acc =
        match insts with
            | [] -> reverse(acc)
            | inst :: tail ->
                match inst with
                    | ConstInt(dest, v) ->
                        reduceLoop(find)(tail)(setInt(dest)(v)(knownInts))(savedInts)(false)(inst :: acc)
                    | Label(name) ->
                        let nextInts =
                            if prevTerm
                            then
                                match find(name)(savedInts) with
                                    | Some(si) -> si
                                    | None -> []
                            else knownInts
                        in
                            reduceLoop(find)(tail)(nextInts)(removeAssociation(name)(savedInts))(false)(inst :: acc)
                    | Jump(target) ->
                        let nextSaved = setSaved(target)(knownInts)(savedInts)
                        in reduceLoop(find)(tail)(knownInts)(nextSaved)(true)(inst :: acc)
                    | JumpIfFalse(_, target) ->
                        let nextSaved = setSaved(target)(knownInts)(savedInts)
                        in reduceLoop(find)(tail)(knownInts)(nextSaved)(false)(inst :: acc)
                    | _ -> reduceLoop(find)(tail)(knownInts)(savedInts)(false)(inst :: acc)
    in reduceLoop(lookupAssociation)(instructions)([])([])(false)([]))

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
