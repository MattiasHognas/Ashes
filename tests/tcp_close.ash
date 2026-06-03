// tcp-server: accept
// expect: ok
Ashes.IO.print(match Ashes.Async.run(async(match await Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(_) -> "fail"
    | Ok(sock) -> 
        match await Ashes.Net.Tcp.close(sock) with
            | Ok(_) -> "ok"
            | Error(_) -> "fail")) with
    | Ok(text) -> text
    | Error(_) -> "fail")
