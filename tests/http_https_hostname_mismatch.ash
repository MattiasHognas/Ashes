// tls-server: accept
// skip-on: win-x64
// The loopback TLS fixture is unsupported under the local Wine smoke-test
// (incomplete TLS/loopback in Wine); win-x64 TLS is covered by the native
// Windows CI runner. See http_https_not_supported.ash.
// tls-cert-host: localhost
// tls-handshake: failure
// expect: Ashes TLS handshake failed: invalid peer certificate: NotValidForName
Ashes.IO.print(match Ashes.Task.run(async await Ashes.Net.Http.get("https://127.0.0.1:__TCP_PORT__/")) with
    | Ok(Ok(text)) -> text
    | Ok(Error(err)) -> err
    | Error(err) -> err)
