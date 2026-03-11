// tcp-server: accept
// expect: ok
match Ashes.Net.Tcp.connect("localhost")(__TCP_PORT__) with
    | Error(_) -> Ashes.IO.print("fail")
    | Ok(sock) -> 
        match Ashes.Net.Tcp.close(sock) with
            | Ok(_) -> Ashes.IO.print("ok")
            | Error(_) -> Ashes.IO.print("fail")
