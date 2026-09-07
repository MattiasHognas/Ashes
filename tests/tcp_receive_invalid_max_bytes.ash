// tcp-server: accept
// expect: error
Ashes.IO.print(match (match await Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(_) -> "error"
    | Ok(sock) ->
        match await Ashes.Net.Tcp.receive(sock)(0) with
            | Ok(_) -> "fail"
            | Error(_) -> "error")
|> async
|> Ashes.Task.run with
    | Ok(text) -> text
    | Error(_) -> "error")
