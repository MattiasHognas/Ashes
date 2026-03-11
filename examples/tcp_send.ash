match Ashes.Net.Tcp.connect("127.0.0.1")(8080) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(sock) -> 
        match Ashes.Net.Tcp.send(sock)("hello") with
            | Ok(n) -> Ashes.IO.print(n)
            | Error(msg) -> Ashes.IO.print(msg)
