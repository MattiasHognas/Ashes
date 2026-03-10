// tcp-server: accept
// expect: ok
match Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(_) -> Ashes.IO.print("fail")
    | Ok(sock) -> 
        match Ashes.Net.Tcp.close(sock) with
            | Ok(_) -> Ashes.IO.print("ok")
            | Error(_) -> Ashes.IO.print("fail")
