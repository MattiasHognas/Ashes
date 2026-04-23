// tcp-server: accept
// tcp-expect: hello
// expect: ok
Ashes.IO.print(match Ashes.Async.run(async
    let sock = await Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__)
    in 
        let n = await Ashes.Net.Tcp.send(sock)("hello")
        in 
            if n == 5
            then "ok"
            else "fail") with
    | Ok(text) -> text
    | Error(_) -> "fail")
