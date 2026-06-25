let rec rpcStartsWith = 
    fun (text) -> 
        fun (prefix) -> 
            match Ashes.Text.uncons(prefix) with
                | None -> true
                | Some((ph, pt)) -> 
                    match Ashes.Text.uncons(text) with
                        | None -> false
                        | Some((th, tt)) -> 
                            if th == ph
                            then rpcStartsWith(tt)(pt)
                            else false
in 
    let rec rpcDrop = 
        fun (text) -> 
            fun (n) -> 
                if n <= 0
                then text
                else 
                    match Ashes.Text.uncons(text) with
                        | None -> ""
                        | Some((_h, t)) -> rpcDrop(t)(n - 1)
    in 
        let rec rpcStrLen = 
            fun (text) -> 
                match Ashes.Text.uncons(text) with
                    | None -> 0
                    | Some((_h, t)) -> 1 + rpcStrLen(t)
        in 
            let rec rpcTrimStart = 
                fun (text) -> 
                    match Ashes.Text.uncons(text) with
                        | None -> ""
                        | Some((h, t)) -> 
                            if h == " "
                            then rpcTrimStart(t)
                            else 
                                if h == "\t"
                                then rpcTrimStart(t)
                                else text
            in 
                let parseContentLength = 
                    fun (line) -> 
                        let prefix = "Content-Length: "
                        in 
                            if rpcStartsWith(line)(prefix)
                            then 
                                let valueStr = rpcDrop(line)(rpcStrLen(prefix))
                                in 
                                    match Ashes.Text.parseInt(rpcTrimStart(valueStr)) with
                                        | Ok(n) -> Some(n)
                                        | Error(_) -> None
                            else None
                in 
                    let rec readHeaders = 
                        fun (contentLength) -> 
                            match Ashes.IO.readLine(Unit) with
                                | None -> Error("unexpected EOF reading RPC headers")
                                | Some(line) -> 
                                    if line == ""
                                    then 
                                        if contentLength < 0
                                        then Error("missing Content-Length header")
                                        else Ok(contentLength)
                                    else 
                                        match parseContentLength(line) with
                                            | Some(n) -> readHeaders(n)
                                            | None -> readHeaders(contentLength)
                    in 
                        let readMessage = 
                            fun (unit) -> 
                                match readHeaders(-1) with
                                    | Error(e) -> Error(e)
                                    | Ok(n) -> 
                                        if n < 0
                                        then Error("invalid Content-Length")
                                        else Ashes.IO.readExact(n)
                        in 
                            let writeMessage = 
                                fun (msg) -> 
                                    let len = Ashes.Text.byteLength(msg)
                                    in 
                                        let header = "Content-Length: " + Ashes.Text.fromInt(len) + "\r\n\r\n"
                                        in 
                                            let _h = Ashes.IO.write(header)
                                            in Ashes.IO.write(msg)
                            in readMessage
