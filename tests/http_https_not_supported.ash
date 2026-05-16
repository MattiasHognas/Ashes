// expect: Ashes.Net.Tcp.connect() failed
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Http.get("https://localhost:1/")) with
    | Ok(text) -> text
    | Error(err) -> err)
