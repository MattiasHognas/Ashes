match Ashes.Net.Tcp.connect("127.0.0.1")(8080) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(sock) -> 
        match Ashes.Net.Tcp.receive(sock)(64) with
            | Ok(text) -> Ashes.IO.print(text)
            | Error(msg) -> Ashes.IO.print(msg)
