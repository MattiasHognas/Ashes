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
    Ashes.IO.write(if Unit
    |> Ashes.IO.Environment.currentDirectory
    |> resultHasText
    then "true"
    else "false")
in
    let _ =
        Ashes.IO.write(if Unit
        |> Ashes.IO.Environment.executableDirectory
        |> resultHasText
        then "true"
        else "false")
    in
        let _ =
            Ashes.IO.write(if Unit
            |> Ashes.IO.Environment.temporaryDirectory
            |> resultHasText
            then "true"
            else "false")
        in
            let _ =
                Ashes.IO.write(if Unit
                |> Ashes.IO.Environment.cacheDirectory
                |> resultHasText
                then "true"
                else "false")
            in
                let _ =
                    Ashes.IO.write(if "PATH"
                    |> Ashes.IO.Environment.get
                    |> environmentHasText
                    then "true"
                    else "false")
                in
                    Ashes.IO.print(if "ASHES_TEST_VARIABLE_THAT_MUST_NOT_EXIST_7A8EC7E8"
                    |> Ashes.IO.Environment.get
                    |> environmentIsMissing
                    then "true"
                    else "false")
