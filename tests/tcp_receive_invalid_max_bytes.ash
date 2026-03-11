// tcp-server: accept
// expect: error
match Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(_) -> Ashes.IO.print("fail")
    | Ok(sock) -> 
        match Ashes.Net.Tcp.receive(sock)(0) with
            | Ok(_) -> Ashes.IO.print("fail")
            | Error(_) -> Ashes.IO.print("error")
