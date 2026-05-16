// tls-server: accept
// tls-cert-host: localhost
// tls-handshake: failure
// expect: Ashes TLS handshake failed
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Http.get("https://127.0.0.1:__TCP_PORT__/")) with
    | Ok(text) -> text
    | Error(err) -> err)
