Ashes.IO.print(match await Ashes.Net.Tcp.connect("127.0.0.1")(8080) with
    | Error(msg) -> msg
    | Ok(sock) ->
        match await Ashes.Net.Tcp.close(sock) with
            | Ok(_) -> "connected"
            | Error(msg) -> msg)
