// tls-server: accept
// skip-on: win-x64
// The loopback TLS fixture is unsupported under the local Wine smoke-test
// (incomplete TLS/loopback in Wine); win-x64 TLS is covered by the native
// Windows CI runner. See http_https_not_supported.ash.
// tls-trust: untrusted
// tls-handshake: failure
// expect: Ashes TLS handshake failed: invalid peer certificate: UnknownIssuer
Ashes.IO.print(match Ashes.Async.run(async await Ashes.Http.get("https://localhost:__TCP_PORT__/")) with
    | Ok(Ok(text)) -> text
    | Ok(Error(err)) -> err
    | Error(err) -> err)
