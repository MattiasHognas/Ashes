// expect: 3 20
import Ashes.Collection.Map
import Ashes.IO
import Ashes.Text
import Ashes.Trait
let compare left right =
    if left == right
    then 0
    else
        if left <= right
        then -1
        else 1
in
    let recursive fill current limit map =
        if current > limit
        then map
        else fill(current + 1)(limit)(Ashes.Collection.Map.setWith(compare)(current)(current * 10)(map))
    in
        let result = fill(0)(2)(Ashes.Collection.Map.empty)
        in
            match Ashes.Collection.Map.getWith(compare)(2)(result) with
                | None -> Ashes.IO.print("missing")
                | Some(value) -> Ashes.IO.print(Ashes.Text.fromInt(Ashes.Collection.Map.size(result)) + " " + Ashes.Text.fromInt(value))
