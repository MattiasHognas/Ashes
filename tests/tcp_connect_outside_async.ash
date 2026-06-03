// tcp-server: accept
// expect: ok
match await Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(sock) -> 
        match await Ashes.Net.Tcp.close(sock) with
            | Ok(_) -> Ashes.IO.print("ok")
            | Error(msg) -> Ashes.IO.print(msg)
