// tcp-server: accept
// tcp-send: abc
// expect: abc
match Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(_) -> Ashes.IO.print("fail")
    | Ok(sock) -> 
        match Ashes.Net.Tcp.receive(sock)(3) with
            | Ok(text) -> Ashes.IO.print(text)
            | Error(_) -> Ashes.IO.print("fail")
