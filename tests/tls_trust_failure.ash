// tls-server: accept
// tls-trust: untrusted
// tls-handshake: failure
// expect: Ashes TLS handshake failed
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Net.Tls.connect("localhost")(__TCP_PORT__)) with
    | Ok(_) -> "fail"
    | Error(err) -> err)
