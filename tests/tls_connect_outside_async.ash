// tls-server: accept
// skip-on: win-x64
// The loopback TLS fixture is unsupported under the local Wine smoke-test
// (incomplete TLS/loopback in Wine); win-x64 TLS is covered by the native
// Windows CI runner. See http_https_not_supported.ash.
// expect: ok
match await Ashes.Net.Tls.connect("localhost")(__TCP_PORT__) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(sock) ->
        match await Ashes.Net.Tls.close(sock) with
            | Ok(_) -> Ashes.IO.print("ok")
            | Error(msg) -> Ashes.IO.print(msg)
