// expect: ok
import Ashes.Map
import Ashes.IO
import Ashes.Text
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
    let map = Ashes.Map.set(compareInt)(3)("three")(Ashes.Map.set(compareInt)(1)("one")(Ashes.Map.set(compareInt)(2)("two updated")(Ashes.Map.empty)))
    in 
        let fromListMap = Ashes.Map.fromList(compareInt)((4, "four") :: (2, "two") :: [])
        in 
            let merged = Ashes.Map.set(compareInt)(4)("four")(map)
            in 
                let summary = 
                    Ashes.Map.foldLeft(fun (acc) -> 
                        fun (key) -> 
                            fun (value) -> acc + Ashes.Text.fromInt(key) + "=" + value + ";")("")(merged)
                in 
                    match Ashes.Map.get(compareInt)(2)(merged) with
                        | None -> Ashes.IO.print("fail")
                        | Some(two) -> 
                            match Ashes.Map.get(compareInt)(4)(fromListMap) with
                                | None -> Ashes.IO.print("fail")
                                | Some(four) -> 
                                    if Ashes.Map.contains(compareInt)(1)(merged)
                                    then 
                                        if Ashes.Map.contains(compareInt)(5)(merged)
                                        then Ashes.IO.print("fail")
                                        else 
                                            if Ashes.Map.isEmpty(Ashes.Map.empty)
                                            then 
                                                if Ashes.Map.isEmpty(merged)
                                                then Ashes.IO.print("fail")
                                                else 
                                                    if Ashes.Map.size(merged) == 4
                                                    then 
                                                        if Ashes.Map.size(fromListMap) == 2
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
