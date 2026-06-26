// tls-server: accept
// skip-on: win-x64
// The loopback TLS fixture is unsupported under the local Wine smoke-test
// (incomplete TLS/loopback in Wine); win-x64 TLS is covered by the native
// Windows CI runner. See http_https_not_supported.ash.
// tls-expect: GET /hello HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n
// tls-send: HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from https
// expect: hello from https
Ashes.IO.print(match Ashes.Async.run(async await Ashes.Http.get("https://localhost:__TCP_PORT__/hello")) with
    | Ok(Ok(text)) -> text
    | Ok(Error(err)) -> err
    | Error(err) -> err)
