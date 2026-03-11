// tcp-server: accept
// tcp-expect: hello
// expect: ok
match Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(_) -> Ashes.IO.print("fail")
    | Ok(sock) -> 
        match Ashes.Net.Tcp.send(sock)("hello") with
            | Error(_) -> Ashes.IO.print("fail")
            | Ok(n) -> 
                if n == 5
                then Ashes.IO.print("ok")
                else Ashes.IO.print("fail")
