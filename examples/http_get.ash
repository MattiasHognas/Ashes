match Ashes.Http.get("http://example.com/") with
    | Ok(body) -> Ashes.IO.print(body)
    | Error(err) -> Ashes.IO.print(err)
