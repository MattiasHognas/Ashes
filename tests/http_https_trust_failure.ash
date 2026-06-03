// tls-server: accept
// tls-trust: untrusted
// tls-handshake: failure
// expect: Ashes TLS handshake failed: invalid peer certificate: UnknownIssuer
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Http.get("https://localhost:__TCP_PORT__/")) with
    | Ok(Ok(text)) -> text
    | Ok(Error(err)) -> err
    | Error(err) -> err)
