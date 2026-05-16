// tls-server: accept
// tls-expect: ping
// tls-send: pong
// expect: ok
Ashes.IO.print(match Ashes.Async.run(async
    let sock = await Ashes.Net.Tls.connect("localhost")(__TCP_PORT__)
    in 
        let sent = await Ashes.Net.Tls.send(sock)("ping")
        in 
            let text = await Ashes.Net.Tls.receive(sock)(64)
            in 
                let _ = await Ashes.Net.Tls.close(sock)
                in 
                    if sent == 4
                    then 
                        if text == "pong"
                        then "ok"
                        else "fail"
                    else "fail") with
    | Ok(text) -> text
    | Error(err) -> err)
