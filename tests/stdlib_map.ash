// expect: ok
import Ashes.Collection.Map
import Ashes.IO
import Ashes.Text
let compareInt left right =
    if left == right
    then 0
    else
        if left <= right
        then -1
        else 1
in
    let map = Ashes.Collection.Map.set(compareInt)(3)("three")(Ashes.Collection.Map.set(compareInt)(1)("one")(Ashes.Collection.Map.set(compareInt)(2)("two updated")(Ashes.Collection.Map.empty)))
    in
        let fromListMap = Ashes.Collection.Map.fromList(compareInt)((4, "four") :: (2, "two") :: [])
        in
            let merged = Ashes.Collection.Map.set(compareInt)(4)("four")(map)
            in
                let summary =
                    Ashes.Collection.Map.foldLeft(given (acc) ->
                        given (key) ->
                            given (value) -> acc + Ashes.Text.fromInt(key) + "=" + value + ";")("")(merged)
                in
                    match Ashes.Collection.Map.get(compareInt)(2)(merged) with
                        | None -> Ashes.IO.print("fail")
                        | Some(two) ->
                            match Ashes.Collection.Map.get(compareInt)(4)(fromListMap) with
                                | None -> Ashes.IO.print("fail")
                                | Some(four) ->
                                    if Ashes.Collection.Map.contains(compareInt)(1)(merged)
                                    then
                                        if Ashes.Collection.Map.contains(compareInt)(5)(merged)
                                        then Ashes.IO.print("fail")
                                        else
                                            if Ashes.Collection.Map.isEmpty(Ashes.Collection.Map.empty)
                                            then
                                                if Ashes.Collection.Map.isEmpty(merged)
                                                then Ashes.IO.print("fail")
                                                else
                                                    if Ashes.Collection.Map.size(merged) == 4
                                                    then
                                                        if Ashes.Collection.Map.size(fromListMap) == 2
                                                        then
                                                            if two == "two updated"
                                                            then
                                                                if four == "four"
                                                                then
                                                                    if summary == "1=one;2=two updated;3=three;4=four;"
                                                                    then Ashes.IO.print("ok")
                                                                    else Ashes.IO.print("fail")
                                                                else Ashes.IO.print("fail")
                                                            else Ashes.IO.print("fail")
                                                        else Ashes.IO.print("fail")
                                                    else Ashes.IO.print("fail")
                                            else Ashes.IO.print("fail")
                                    else Ashes.IO.print("fail")
