// tcp-server: accept
// expect: ok
Ashes.IO.print(match (match await Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(_) -> "fail"
    | Ok(sock) ->
        match await Ashes.Net.Tcp.close(sock) with
            | Ok(_) -> "ok"
            | Error(_) -> "fail")
|> async
|> Ashes.Task.run with
    | Ok(text) -> text
    | Error(_) -> "fail")
