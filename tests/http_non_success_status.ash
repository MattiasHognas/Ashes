// tcp-server: accept
// tcp-expect: GET /missing HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n
// tcp-send: HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\nmissing
// expect: HTTP 404
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Http.get("http://127.0.0.1:__TCP_PORT__/missing")) with
    | Ok(text) -> text
    | Error(err) -> err)
