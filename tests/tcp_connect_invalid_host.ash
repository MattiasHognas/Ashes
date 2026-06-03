// expect: error
Ashes.IO.print(match Ashes.Async.run(async
    match await Ashes.Net.Tcp.connect("not-a-host")(80) with
        | Ok(_) -> "fail"
        | Error(_) -> "error") with
    | Ok(text) -> text
    | Error(_) -> "error")
