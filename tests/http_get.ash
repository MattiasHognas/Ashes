// tcp-server: accept
// tcp-expect: GET /hello HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n
// tcp-send: HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from http
// expect: hello from http
match Ashes.Http.get("http://127.0.0.1:__TCP_PORT__/hello") with
    | Ok(text) -> Ashes.IO.print(text)
    | Error(err) -> Ashes.IO.print(err)
