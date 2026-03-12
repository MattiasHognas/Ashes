// expect: ok
import Ashes.Result
import Ashes.IO
let mapped = 
    Ashes.Result.map(fun (x) -> x + 1)(Ok(41))
in 
    let flatMapped = 
        Ashes.Result.flatMap(fun (x) -> Ok(x + 1))(Ok(41))
    in 
        let fallback = Ashes.Result.getOrElse(99)(Error("bad"))
        in 
            let okState = Ashes.Result.isOk(Ok(1))
            in 
                let errorState = Ashes.Result.isError(Error("bad"))
                in 
                    match mapped with
                        | Error(_) -> Ashes.IO.print("fail")
                        | Ok(mappedValue) -> 
                            match flatMapped with
                                | Error(_) -> Ashes.IO.print("fail")
                                | Ok(flatMappedValue) -> 
                                    if mappedValue == 42
                                    then 
                                        if flatMappedValue == 42
                                        then 
                                            if fallback == 99
                                            then 
                                                if okState
                                                then 
                                                    if errorState
                                                    then Ashes.IO.print("ok")
                                                    else Ashes.IO.print("fail")
                                                else Ashes.IO.print("fail")
                                            else Ashes.IO.print("fail")
                                        else Ashes.IO.print("fail")
                                    else Ashes.IO.print("fail")
