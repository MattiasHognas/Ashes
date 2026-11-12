// expect: done
import Ashes.Collection.Map.MapTree
let lookup (label: Str) (pairs: MapTree(Str, Str)) = Ashes.Collection.Map.getStr(label)(pairs)

let recursive follow (count: Int) (label: Str) (pairs: MapTree(Str, Str)) =
    if count <= 0
    then Some(label)
    else
        match lookup(label)(pairs) with
            | Some(next) -> follow(count - 1)(next)(pairs)
            | None -> None

let fresh seed =
    (let text = "label" + Ashes.Text.fromInt(seed)
    in
        let pairs = Ashes.Collection.Map.setStr("start")(text)(Ashes.Collection.Map.empty)
        in follow(1)("start")(pairs) == Some(text))

let recursive run count =
    if count == 0
    then Ashes.IO.print("done")
    else
        if fresh(count)
        then run(count - 1)
        else Ashes.IO.panic("wrong result")

run(1000)
