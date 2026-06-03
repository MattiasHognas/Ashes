// tls-server: accept
// tls-cert-host: localhost
// tls-handshake: failure
// expect: Ashes TLS handshake failed: invalid peer certificate: NotValidForName
Ashes.IO.print(match Ashes.Async.run(async
    match await Ashes.Net.Tls.connect("127.0.0.1")(__TCP_PORT__) with
        | Ok(_) -> "fail"
        | Error(err) -> err) with
    | Ok(text) -> text
    | Error(err) -> err)
