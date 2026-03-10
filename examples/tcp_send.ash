match Ashes.Net.Tcp.connect("127.0.0.1")(8080) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(sock) -> 
        match Ashes.Net.Tcp.send(sock)("hello") with
            | Ok(_) -> Ashes.IO.print("sent")
            | Error(msg) -> Ashes.IO.print(msg)
