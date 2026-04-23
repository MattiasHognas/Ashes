// tcp-server: accept
// tcp-send: hello
// expect: hello
Ashes.IO.print(match Ashes.Async.run(async
    let sock = await Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__)
    in await Ashes.Net.Tcp.receive(sock)(64)) with
    | Ok(text) -> text
    | Error(_) -> "fail")
