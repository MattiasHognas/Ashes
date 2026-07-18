// tls-server: accept
// skip-on: win-x64
// The loopback TLS fixture is unsupported under the local Wine smoke-test
// (incomplete TLS/loopback in Wine); win-x64 TLS is covered by the native
// Windows CI runner. See http_https_not_supported.ash.
// tls-trust: untrusted
// tls-handshake: failure
// expect: Ashes TLS handshake failed: invalid peer certificate: UnknownIssuer
Ashes.IO.print(match Ashes.Task.run(async(match await Ashes.Net.Tls.connect("localhost")(__TCP_PORT__) with
    | Ok(_) -> "fail"
    | Error(err) -> err)) with
    | Ok(text) -> text
    | Error(err) -> err)
