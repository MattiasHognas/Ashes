// tcp-server: accept
// tcp-expect: POST /echo HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello
// tcp-send: HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nposted
// expect: posted
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Http.post("http://127.0.0.1:__TCP_PORT__/echo")("hello")) with
    | Ok(text) -> text
    | Error(err) -> err)
