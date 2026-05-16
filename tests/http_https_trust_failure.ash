// tls-server: accept
// tls-trust: untrusted
// tls-handshake: failure
// expect: Ashes TLS handshake failed
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Http.get("https://localhost:__TCP_PORT__/")) with
    | Ok(text) -> text
    | Error(err) -> err)
