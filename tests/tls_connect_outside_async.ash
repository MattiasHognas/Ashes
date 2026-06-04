// tls-server: accept
// expect: ok
match await Ashes.Net.Tls.connect("localhost")(__TCP_PORT__) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(sock) -> 
        match await Ashes.Net.Tls.close(sock) with
            | Ok(_) -> Ashes.IO.print("ok")
            | Error(msg) -> Ashes.IO.print(msg)
