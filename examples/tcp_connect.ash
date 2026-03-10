match Ashes.Net.Tcp.connect("127.0.0.1")(8080) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(sock) -> 
        match Ashes.Net.Tcp.close(sock) with
            | Ok(_) -> Ashes.IO.print("connected")
            | Error(msg) -> Ashes.IO.print(msg)
