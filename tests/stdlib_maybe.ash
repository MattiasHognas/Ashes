// expect: ok
import Ashes.Maybe
import Ashes.IO
let mapped = 
    Ashes.Maybe.map(fun (x) -> x + 1)(Some(41))
in 
    let flatMapped = 
        Ashes.Maybe.flatMap(fun (x) -> Some(x + 1))(Some(41))
    in 
        let fallback = Ashes.Maybe.getOrElse(99)(None)
        in 
            let hasValue = Ashes.Maybe.isSome(Some(1))
            in 
                let missingValue = Ashes.Maybe.isNone(None)
                in 
                    match mapped with
                        | None -> Ashes.IO.print("fail")
                        | Some(mappedValue) -> 
                            match flatMapped with
                                | None -> Ashes.IO.print("fail")
                                | Some(flatMappedValue) -> 
                                    if mappedValue == 42
                                    then 
                                        if flatMappedValue == 42
                                        then 
                                            if fallback == 99
                                            then 
                                                if hasValue
                                                then 
                                                    if missingValue
                                                    then Ashes.IO.print("ok")
                                                    else Ashes.IO.print("fail")
                                                else Ashes.IO.print("fail")
                                            else Ashes.IO.print("fail")
                                        else Ashes.IO.print("fail")
                                    else Ashes.IO.print("fail")
