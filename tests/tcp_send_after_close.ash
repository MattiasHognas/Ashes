// expect-compile-error: ASH006
// tcp-server: accept
Ashes.IO.print(match Ashes.Async.run(async
    let sock = await Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__)
    in 
        let _ = await Ashes.Net.Tcp.close(sock)
        in 
            let _ = await Ashes.Net.Tcp.send(sock)("x")
            in "fail") with
    | Ok(text) -> text
    | Error(_) -> "error")
