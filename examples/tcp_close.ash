Ashes.IO.print(match Ashes.Async.run(async
    let sock = await Ashes.Net.Tcp.connect("127.0.0.1")(8080)
    in 
        let _ = await Ashes.Net.Tcp.close(sock)
        in "closed") with
    | Ok(text) -> text
    | Error(msg) -> msg)
