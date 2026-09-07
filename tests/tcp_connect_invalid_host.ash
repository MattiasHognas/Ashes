// expect: error
Ashes.IO.print(match (match await Ashes.Net.Tcp.connect("not-a-host")(80) with
    | Ok(_) -> "fail"
    | Error(_) -> "error")
|> async
|> Ashes.Task.run with
    | Ok(text) -> text
    | Error(_) -> "error")
