// expect: ok
import Ashes.Array
import Ashes.IO
let xs = Ashes.Array.fromList(10 :: 20 :: 30 :: [])
in 
    let updated = Ashes.Array.set(1)(25)(xs)
    in 
        let appended = Ashes.Array.append(40)(updated)
        in 
            match Ashes.Array.get(0)(appended) with
                | None -> Ashes.IO.print("fail")
                | Some(first) -> 
                    match Ashes.Array.get(1)(appended) with
                        | None -> Ashes.IO.print("fail")
                        | Some(second) -> 
                            match Ashes.Array.get(3)(appended) with
                                | None -> Ashes.IO.print("fail")
                                | Some(last) -> 
                                    match Ashes.Array.get(4)(appended) with
                                        | None -> 
                                            if Ashes.Array.length(appended) == 4
                                            then 
                                                if Ashes.Array.isEmpty(Ashes.Array.empty)
                                                then 
                                                    if Ashes.Array.isEmpty(appended)
                                                    then Ashes.IO.print("fail")
                                                    else 
                                                        if first == 10
                                                        then 
                                                            if second == 25
                                                            then 
                                                                if last == 40
                                                                then Ashes.IO.print("ok")
                                                                else Ashes.IO.print("fail")
                                                            else Ashes.IO.print("fail")
                                                        else Ashes.IO.print("fail")
                                                else Ashes.IO.print("fail")
                                            else Ashes.IO.print("fail")
                                        | Some(_) -> Ashes.IO.print("fail")
