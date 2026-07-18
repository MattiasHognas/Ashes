// expect-compile-error: ASH006
// tcp-server: accept
Ashes.IO.print(match Ashes.Task.run(async(match await Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(_) -> "error"
    | Ok(sock) ->
        let _ = await Ashes.Net.Tcp.close(sock)
        in
            let _ = await Ashes.Net.Tcp.send(sock)("x")
            in "fail")) with
    | Ok(text) -> text
    | Error(_) -> "error")
