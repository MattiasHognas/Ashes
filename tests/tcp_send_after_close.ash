// tcp-server: accept
// expect: error
match Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(_) -> Ashes.IO.print("fail")
    | Ok(sock) -> 
        match Ashes.Net.Tcp.close(sock) with
            | Error(_) -> Ashes.IO.print("fail")
            | Ok(_) -> 
                match Ashes.Net.Tcp.send(sock)("x") with
                    | Ok(_) -> Ashes.IO.print("fail")
                    | Error(_) -> Ashes.IO.print("error")
