// expect: truetruetruetruetruetrue
import Ashes.Text
let resultHasText =
    given (result) ->
        match result with
            | Ok(value) -> Ashes.Text.length(value) > 0
            | Error(_) -> false

let environmentHasText =
    given (result) ->
        match result with
            | Ok(Some(value)) -> Ashes.Text.length(value) > 0
            | Ok(None) -> false
            | Error(_) -> false

let environmentIsMissing =
    given (result) ->
        match result with
            | Ok(None) -> true
            | Ok(Some(_)) -> false
            | Error(_) -> false

let _ =
    Ashes.IO.write(if resultHasText(Ashes.IO.Environment.currentDirectory(Unit))
    then "true"
    else "false")
in
    let _ =
        Ashes.IO.write(if resultHasText(Ashes.IO.Environment.executableDirectory(Unit))
        then "true"
        else "false")
    in
        let _ =
            Ashes.IO.write(if resultHasText(Ashes.IO.Environment.temporaryDirectory(Unit))
            then "true"
            else "false")
        in
            let _ =
                Ashes.IO.write(if resultHasText(Ashes.IO.Environment.cacheDirectory(Unit))
                then "true"
                else "false")
            in
                let _ =
                    Ashes.IO.write(if environmentHasText(Ashes.IO.Environment.get("PATH"))
                    then "true"
                    else "false")
                in
                    Ashes.IO.print(if environmentIsMissing(Ashes.IO.Environment.get("ASHES_TEST_VARIABLE_THAT_MUST_NOT_EXIST_7A8EC7E8"))
                    then "true"
                    else "false")
