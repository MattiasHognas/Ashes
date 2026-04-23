// tcp-server: accept
// tcp-expect: GET /hello HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n
// tcp-send: HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from http
// expect: hello from http
let task = 
    async
        let response = await Ashes.Http.get("http://127.0.0.1:__TCP_PORT__/hello")
        in response
in 
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(text) -> text
        | Error(err) -> err)
