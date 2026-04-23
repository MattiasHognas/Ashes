// expect: https not supported
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Http.get("https://example.com")) with
    | Ok(text) -> text
    | Error(err) -> err)
