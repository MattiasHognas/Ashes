Ashes.IO.print(match await Ashes.Net.Tls.connect("example.com")(443) with
    | Error(msg) -> msg
    | Ok(sock) ->
        match await Ashes.Net.Tls.send(sock)("GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n") with
            | Error(msg) -> msg
            | Ok(_) ->
                match await Ashes.Net.Tls.receive(sock)(4096) with
                    | Error(msg) -> msg
                    | Ok(response) ->
                        match await Ashes.Net.Tls.close(sock) with
                            | Error(msg) -> msg
                            | Ok(_) -> response)
