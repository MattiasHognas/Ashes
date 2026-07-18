// expect: ok
import Ashes.Core.Result
import Ashes.IO
let mapped =
    Ashes.Core.Result.map(given (x) -> x + 1)(Ok(41))
in
    let flatMapped =
        Ashes.Core.Result.flatMap(given (x) -> Ok(x + 1))(Ok(41))
    in
        let fallback = Ashes.Core.Result.getOrElse(99)(Error("bad"))
        in
            let okState = Ashes.Core.Result.isOk(Ok(1))
            in
                let errorState = Ashes.Core.Result.isError(Error("bad"))
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
