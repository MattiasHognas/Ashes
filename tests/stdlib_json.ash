// expect: ok
import Ashes.Text.Json
match Ashes.Text.Json.parse("null") with
    | Error(e) -> Ashes.IO.print(e)
    | Ok(json) ->
        if Ashes.Text.Json.isNull(json)
        then
            match Ashes.Text.Json.parse("{\"x\":1}") with
                | Error(e) -> Ashes.IO.print(e)
                | Ok(obj) ->
                    match Ashes.Text.Json.get("x")(obj) with
                        | Error(e) -> Ashes.IO.print(e)
                        | Ok(val) ->
                            match Ashes.Text.Json.asInt(val) with
                                | Error(e) -> Ashes.IO.print(e)
                                | Ok(n) ->
                                    if n == 1
                                    then Ashes.IO.print("ok")
                                    else Ashes.IO.print("fail")
        else Ashes.IO.print("fail")
