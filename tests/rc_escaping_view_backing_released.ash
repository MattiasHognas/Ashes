// expect: body=[ab] sep=[,] body=[wx] sep=[,]

let recursive skipWs text =
    match Ashes.Text.unconsText(text) with
        | None -> ""
        | Some((h, t)) ->
            if h == " "
            then skipWs(t)
            else text

let splitAtQuote text =
    (let bytes = Ashes.Byte.fromText(text)
    in
        let total = Ashes.Byte.length(bytes)
        in
            let quote = Ashes.Byte.indexOf(bytes)(34)(0)
            in
                if quote < 0
                then None
                else Some((Ashes.Byte.subText(bytes)(0)(quote), Ashes.Byte.subView(bytes)(quote + 1)(total - quote - 1))))

let readQuoted text =
    match Ashes.Text.unconsText(text) with
        | None -> Error("expected a quote")
        | Some((h, t)) ->
            if h == "\""
            then
                match splitAtQuote(t) with
                    | Some(parts) -> Ok(parts)
                    | None -> Error("unterminated")
            else Error("expected a quote to open the string")

let readValue text =
    (let trimmed = skipWs(text)
    in
        match readQuoted(trimmed) with
            | Error(e) -> Error(e)
            | Ok((body, after)) -> Ok((body, after)))

let recursive grow (n: Int) (acc: Str) =
    if n == 0
    then acc
    else grow(n - 1)(acc + "z")

let readOne cur =
    match readValue(cur) with
        | Error(e) -> "err(" + e + ")"
        | Ok((body, after)) ->
            let filler = grow(40)("")
            in
                let rest = skipWs(after)
                in
                    match Ashes.Text.unconsText(rest) with
                        | None ->
                            (if filler == ""
                            then "!"
                            else "") + "body=[" + body + "] end"
                        | Some((sep, _tail)) ->
                            (if filler == ""
                            then "!"
                            else "") + "body=[" + body + "] sep=[" + sep + "]"

Ashes.IO.print(readOne("\"ab\", cd") + " " + readOne("\"wx\", yz"))
