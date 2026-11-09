// expect: 600
// The label's lookup is generic over a comparison closure, so its result stays in the arena. The part
// it hands on is taken as an accumulator edge all the same: the join copies the arena part onto the
// reference-counted heap rather than retaining it, assuming nothing about where the lookup placed it.
import Ashes.Collection.List.reverse
import Ashes.Collection.List.length
import Ashes.Text.fromInt
type Inst =
    | ConstInt(Int, Int)
    | Jump(Str)
    | JumpIfFalse(Int, Str)
    | Label(Str)
    | Other(Int)

let recursive lookupBy same key entries =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if same(k)(key)
            then Some(v)
            else lookupBy(same)(key)(tail)

let sameLabel (a: Str) (b: Str) = a == b

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
                                match lookupBy(sameLabel)(name)(savedInts) with
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
