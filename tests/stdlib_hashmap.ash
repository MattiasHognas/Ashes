// expect: ok
import Ashes.Collection.HashMap
import Ashes.IO
let m = Ashes.Collection.HashMap.set("zebra")(26)(Ashes.Collection.HashMap.set("apple")(1)(Ashes.Collection.HashMap.set("mango")(13)(Ashes.Collection.HashMap.set("apple")(99)(Ashes.Collection.HashMap.empty))))

let appleVal =
    match Ashes.Collection.HashMap.get("apple")(m) with
        | Some(v) -> v
        | None -> -1

let summary =
    Ashes.Collection.HashMap.foldLeft(given (acc) ->
        given (_k) ->
            given (v) -> acc + v)(0)(m)
in
    if Ashes.Collection.HashMap.size(m) == 3
    then
        if appleVal == 1
        then
            if Ashes.Collection.HashMap.contains("mango")(m)
            then
                if Ashes.Collection.HashMap.contains("nope")(m)
                then Ashes.IO.print("fail-contains")
                else
                    if summary == 40
                    then Ashes.IO.print("ok")
                    else Ashes.IO.print("fail-fold")
            else Ashes.IO.print("fail-mango")
        else Ashes.IO.print("fail-apple")
    else Ashes.IO.print("fail-size")
