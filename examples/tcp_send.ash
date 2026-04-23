match Ashes.Async.run(async
    let sock = await Ashes.Net.Tcp.connect("127.0.0.1")(8080)
    in await Ashes.Net.Tcp.send(sock)("hello")) with
    | Ok(n) -> Ashes.IO.print(n)
    | Error(msg) -> Ashes.IO.print(msg)
