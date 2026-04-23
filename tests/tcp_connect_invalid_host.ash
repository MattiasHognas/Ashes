// expect: error
Ashes.IO.print(match Ashes.Async.run(async
    let _ = await Ashes.Net.Tcp.connect("not-a-host")(80)
    in "fail") with
    | Ok(text) -> text
    | Error(_) -> "error")
