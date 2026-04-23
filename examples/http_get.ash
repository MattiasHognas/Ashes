// Requires a plain HTTP/1.1 endpoint that replies without chunked transfer encoding.
// Example local target: python -m http.server 8080 --bind 127.0.0.1
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Http.get("http://127.0.0.1:8080/")) with
    | Ok(body) -> body
    | Error(err) -> err)
