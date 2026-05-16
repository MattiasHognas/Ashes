Ashes.IO.print(match Ashes.Async.run(async
    let sock = await Ashes.Net.Tls.connect("example.com")(443)
    in 
        let _ = await Ashes.Net.Tls.send(sock)("GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n")
        in 
            let response = await Ashes.Net.Tls.receive(sock)(4096)
            in 
                let _ = await Ashes.Net.Tls.close(sock)
                in response) with
    | Ok(text) -> text
    | Error(err) -> err)
