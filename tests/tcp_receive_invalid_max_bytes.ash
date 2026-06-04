// tcp-server: accept
// expect: error
Ashes.IO.print(match Ashes.Async.run(async(match await Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(_) -> "error"
    | Ok(sock) -> 
        match await Ashes.Net.Tcp.receive(sock)(0) with
            | Ok(_) -> "fail"
            | Error(_) -> "error")) with
    | Ok(text) -> text
    | Error(_) -> "error")
