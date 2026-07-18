// expect: a=3 b=5 total=2
import Ashes.IO
import Ashes.Collection.Map
import Ashes.Text
import Ashes.Text
let m1 =
    Ashes.Collection.Map.upsertStr("a")(1)(given (old) -> old + 100)(Ashes.Collection.Map.empty)

let m2 =
    Ashes.Collection.Map.upsertStr("b")(5)(given (old) -> old + 100)(m1)

let m3 =
    Ashes.Collection.Map.upsertStr("a")(9)(given (old) -> old + 2)(m2)

let a =
    match Ashes.Collection.Map.getStr("a")(m3) with
        | Some(v) -> v
        | None -> -1

let b =
    match Ashes.Collection.Map.getStr("b")(m3) with
        | Some(v) -> v
        | None -> -1

Ashes.IO.writeLine("a=" + Ashes.Text.fromInt(a) + " b=" + Ashes.Text.fromInt(b) + " total=" + Ashes.Text.fromInt(Ashes.Collection.Map.size(m3)))
