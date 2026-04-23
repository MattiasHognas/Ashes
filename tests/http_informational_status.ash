// tcp-server: accept
// tcp-expect: GET /switch HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n
// tcp-send: HTTP/1.1 101 Switching Protocols\r\nConnection: close\r\n\r\nswitching
// expect: HTTP 101
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Http.get("http://127.0.0.1:__TCP_PORT__/switch")) with
    | Ok(text) -> text
    | Error(err) -> err)
