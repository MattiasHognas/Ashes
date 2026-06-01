// expect: ok
import Ashes.Map
import Ashes.IO

let compareInt =
    fun (left) ->
        fun (right) ->
            if left == right
            then 0
            else
                if left <= right
                then -1
                else 1
in
    let map =
        Ashes.Map.fromList(compareInt)([(3, "three"), (1, "one"), (2, "two"), (2, "two updated")])
    in
        let map2 = Ashes.Map.set(compareInt)(4)("four")(map)
        in
            let ordered = Ashes.Map.toList(map2)
            in
                match Ashes.Map.get(compareInt)(2)(map2) with
                    | None -> Ashes.IO.print("fail")
                    | Some(two) ->
                        if Ashes.Map.contains(compareInt)(1)(map2)
                        then
                            if Ashes.Map.contains(compareInt)(5)(map2)
                            then Ashes.IO.print("fail")
                            else
                                if Ashes.Map.size(map2) == 4
                                then
                                    match ordered with
                                        | [(1, first), (2, second), (3, third), (4, fourth)] ->
                                            if first == "one"
                                            then
                                                if second == "two updated"
                                                then
                                                    if third == "three"
                                                    then
                                                        if fourth == "four"
                                                        then
                                                            if two == "two updated"
                                                            then Ashes.IO.print("ok")
                                                            else Ashes.IO.print("fail")
                                                        else Ashes.IO.print("fail")
                                                    else Ashes.IO.print("fail")
                                                else Ashes.IO.print("fail")
                                            else Ashes.IO.print("fail")
                                        | _ -> Ashes.IO.print("fail")
                                else Ashes.IO.print("fail")
                        else Ashes.IO.print("fail")
