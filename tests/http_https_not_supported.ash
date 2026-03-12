// expect: https not supported
match Ashes.Http.get("https://example.com") with
    | Ok(text) -> Ashes.IO.print(text)
    | Error(err) -> Ashes.IO.print(err)
