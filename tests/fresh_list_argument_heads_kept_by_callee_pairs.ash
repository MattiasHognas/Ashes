// expect: 78
import Ashes.Text
type Op =
    | LabelO(Str)
    | JumpO(Str)

type Inst =
    | op: Op

let mkLabel n = "endif_" + Ashes.Text.fromInt(n)

let recursive buildInsts n acc =
    if n <= 0
    then Inst(op = LabelO(mkLabel(0))) :: acc
    else buildInsts(n - 1)(Inst(op = JumpO(mkLabel(0))) :: acc)

let recursive collectNames xs =
    match xs with
        | [] -> []
        | Inst { op = LabelO(name) } :: rest -> name :: collectNames(rest)
        | _ :: rest -> collectNames(rest)

let recursive pairUp names =
    match names with
        | [] -> []
        | name :: rest -> (name, 7) :: pairUp(rest)

let recursive lookupKey key env =
    match env with
        | [] -> Ashes.IO.panic("unknown index " + key)
        | (k, v) :: rest ->
            if k == key
            then v
            else lookupKey(key)(rest)

let recursive countPairs xs acc =
    match xs with
        | [] -> acc
        | _ :: rest -> countPairs(rest)(acc + 1)

let recursive churnPairs n acc =
    if n <= 0
    then acc
    else churnPairs(n - 1)((mkLabel(n), n) :: acc)

let recursive walk assoc xs total =
    match xs with
        | [] -> total
        | Inst { op = JumpO(l) } :: rest -> walk(assoc)(rest)(total + lookupKey(l)(assoc))
        | Inst { op = LabelO(l) } :: rest -> walk(assoc)(rest)(total + lookupKey(l)(assoc))

let run unit =
    (let xs = buildInsts(3)([])
    in
        let assoc = pairUp(collectNames(xs))
        in
            let noise = churnPairs(50)([])
            in
                let total = walk(assoc)(xs)(0)
                in Ashes.IO.print(Ashes.Text.fromInt(total + countPairs(noise)(0))))

run(Unit)
