// expect: ok
import Ashes.Json
match Ashes.Json.parse("null") with
    | Error(e) -> Ashes.IO.print(e)
    | Ok(json) ->
        if Ashes.Json.isNull(json)
        then
            match Ashes.Json.parse("{\"x\":1}") with
                | Error(e) -> Ashes.IO.print(e)
                | Ok(obj) ->
                    match Ashes.Json.get("x")(obj) with
                        | Error(e) -> Ashes.IO.print(e)
                        | Ok(val) ->
                            match Ashes.Json.asInt(val) with
                                | Error(e) -> Ashes.IO.print(e)
                                | Ok(n) ->
                                    if n == 1
                                    then Ashes.IO.print("ok")
                                    else Ashes.IO.print("fail")
        else Ashes.IO.print("fail")
