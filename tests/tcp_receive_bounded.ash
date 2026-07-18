// tcp-server: accept
// tcp-send: abc
// expect: abc
Ashes.IO.print(match Ashes.Task.run(async(match await Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(_) -> "fail"
    | Ok(sock) ->
        match await Ashes.Net.Tcp.receive(sock)(3) with
            | Ok(text) -> text
            | Error(_) -> "fail")) with
    | Ok(text) -> text
    | Error(_) -> "fail")
