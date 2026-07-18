let recursive rpcStartsWith text prefix =
    match Ashes.Text.uncons(prefix) with
        | None -> true
        | Some((ph, pt)) ->
            match Ashes.Text.uncons(text) with
                | None -> false
                | Some((th, tt)) ->
                    if th == ph
                    then rpcStartsWith(tt)(pt)
                    else false

let recursive rpcDrop text n =
    if n <= 0
    then text
    else
        match Ashes.Text.uncons(text) with
            | None -> ""
            | Some((_h, t)) -> rpcDrop(t)(n - 1)

let recursive rpcStrLen text =
    match Ashes.Text.uncons(text) with
        | None -> 0
        | Some((_h, t)) -> 1 + rpcStrLen(t)

let recursive rpcTrimStart text =
    match Ashes.Text.uncons(text) with
        | None -> ""
        | Some((h, t)) ->
            if h == " "
            then rpcTrimStart(t)
            else
                if h == "\t"
                then rpcTrimStart(t)
                else text

let parseContentLength line =
    (let prefix = "Content-Length: "
    in
        let lb = Ashes.Byte.fromText(line)
        in
            let plen = Ashes.Text.byteLength(prefix)
            in
                let llen = Ashes.Byte.length(lb)
                in
                    if llen < plen
                    then None
                    else
                        if Ashes.Byte.subView(lb)(0)(plen) == prefix
                        then
                            let valueStr = Ashes.Byte.subText(lb)(plen)(llen - plen)
                            in
                                match Ashes.Text.parseInt(rpcTrimStart(valueStr)) with
                                    | Ok(n) -> Some(n)
                                    | Error(_) -> None
                        else None)

let recursive readHeaders contentLength =
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

let readMessage unit =
    match readHeaders(-1) with
        | Error(e) -> Error(e)
        | Ok(n) ->
            if n < 0
            then Error("invalid Content-Length")
            else Ashes.IO.readExact(n)

let writeMessage msg =
    (let len = Ashes.Text.byteLength(msg)
    in
        let header = "Content-Length: " + Ashes.Text.fromInt(len) + "\r\n\r\n"
        in
            let _h = Ashes.IO.write(header)
            in Ashes.IO.write(msg))
