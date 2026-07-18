// tcp-server: accept
// tcp-expect: hello
// expect: ok
Ashes.IO.print(match Ashes.Task.run(async(match await Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__) with
    | Error(_) -> "fail"
    | Ok(sock) ->
        match await Ashes.Net.Tcp.send(sock)("hello") with
            | Ok(n) ->
                if n == 5
                then "ok"
                else "fail"
            | Error(_) -> "fail")) with
    | Ok(text) -> text
    | Error(_) -> "fail")
