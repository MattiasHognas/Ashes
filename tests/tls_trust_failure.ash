// tls-server: accept
// tls-trust: untrusted
// tls-handshake: failure
// expect: Ashes TLS handshake failed: invalid peer certificate: UnknownIssuer
Ashes.IO.print(match Ashes.Async.run(async(match await Ashes.Net.Tls.connect("localhost")(__TCP_PORT__) with
    | Ok(_) -> "fail"
    | Error(err) -> err)) with
    | Ok(text) -> text
    | Error(err) -> err)
