// expect-compile-error: ASH006
Ashes.IO.print(match Ashes.Async.run(async(match await Ashes.Net.Tls.connect("localhost")(443) with
    | Error(_) -> "error"
    | Ok(sock) ->
        let _ = await Ashes.Net.Tls.close(sock)
        in
            let _ = await Ashes.Net.Tls.send(sock)("x")
            in "fail")) with
    | Ok(text) -> text
    | Error(_) -> "error")
