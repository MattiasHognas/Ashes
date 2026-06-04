// tls-server: accept
// tls-expect: ping
// tls-send: pong
// expect: ok
Ashes.IO.print(match Ashes.Async.run(async(match await Ashes.Net.Tls.connect("localhost")(__TCP_PORT__) with
    | Error(err) -> err
    | Ok(sock) -> 
        match await Ashes.Net.Tls.send(sock)("ping") with
            | Error(err) -> err
            | Ok(sent) -> 
                match await Ashes.Net.Tls.receive(sock)(64) with
                    | Error(err) -> err
                    | Ok(text) -> 
                        let _ = await Ashes.Net.Tls.close(sock)
                        in 
                            if sent == 4
                            then 
                                if text == "pong"
                                then "ok"
                                else "fail"
                            else "fail")) with
    | Ok(text) -> text
    | Error(err) -> err)
