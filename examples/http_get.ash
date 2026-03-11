// Requires a plain HTTP/1.1 endpoint that replies without chunked transfer encoding.
// Example local target: python -m http.server 8080 --bind 127.0.0.1
match Ashes.Http.get("http://127.0.0.1:8080/") with
    | Ok(body) -> Ashes.IO.print(body)
    | Error(err) -> Ashes.IO.print(err)
