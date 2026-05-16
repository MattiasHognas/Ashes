// tls-server: accept
// tls-expect: GET /hello HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n
// tls-send: HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from https
// expect: hello from https
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Http.get("https://localhost:__TCP_PORT__/hello")) with
    | Ok(text) -> text
    | Error(err) -> err)
