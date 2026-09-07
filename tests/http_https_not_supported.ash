// expect: Ashes.Net.Tcp.connect() failed
// skip-on: win-x64
// Windows WSAPoll does not reliably surface POLLERR/POLLHUP for a non-blocking
// connect() to a refused localhost port (vs. POSIX poll(2)), so the runtime
// parks forever in WaitSocketWrite. Tracked separately; see examples/https_get.ash.
Ashes.IO.print(match await Ashes.Net.Http.get("https://localhost:1/")
|> async
|> Ashes.Task.run with
    | Ok(Ok(text)) -> text
    | Ok(Error(err)) -> err
    | Error(err) -> err)
