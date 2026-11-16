// A let owning a string whose result is a view of it (the tail `unconsText` hands back), returned
// through an arena Ok out of a local recursive parser: the result is copied before the string is
// released, so the outer parser reads the right separator after a string element.
// expect: ok []

type Json =
    | JStr(Str)
    | JArray(Json, Json)
    | JArrayEnd

let recursive skipWs text =
    match Ashes.Text.unconsText(text) with
        | None -> ""
        | Some((h, t)) ->
            if h == " "
            then skipWs(t)
            else text

let afterQuote text =
    (let tb = Ashes.Byte.fromText(text)
    in
        let q = Ashes.Byte.indexOf(tb)(34)(0)
        in (Ashes.Byte.subText(tb)(0)(q), Ashes.Byte.subView(tb)(q + 1)(Ashes.Byte.length(tb) - q - 1)))

let recursive parseValue text =
    (let trimmed = skipWs(text)
    in
        match Ashes.Text.unconsText(trimmed) with
            | None -> Error("end")
            | Some((h, rest)) ->
                if h == "\""
                then
                    match afterQuote(rest) with
                        | (s, after) -> Ok((JStr(s), after))
                else
                    if h == "["
                    then
                        let recursive parseArr cur =
                            let tc = skipWs(cur)
                            in
                                match Ashes.Text.unconsText(tc) with
                                    | None -> Error("unterminated array")
                                    | Some((c, ctail)) ->
                                        if c == "]"
                                        then Ok((JArrayEnd, ctail))
                                        else
                                            match parseValue(tc) with
                                                | Error(e) -> Error(e)
                                                | Ok((elem, afterElem)) ->
                                                    let ta = skipWs(afterElem)
                                                    in
                                                        match Ashes.Text.unconsText(ta) with
                                                            | None -> Error("unterminated array")
                                                            | Some((sep, sepTail)) ->
                                                                if sep == "]"
                                                                then Ok((JArray(elem)(JArrayEnd), sepTail))
                                                                else
                                                                    if sep == ","
                                                                    then
                                                                        match parseArr(sepTail) with
                                                                            | Error(e) -> Error(e)
                                                                            | Ok((rArr, rAfter)) -> Ok((JArray(elem)(rArr), rAfter))
                                                                    else Error("expected , or ] got [" + sep + "]")
                        in parseArr(rest)
                    else Error("unexpected " + h))

let try text =
    match parseValue(text) with
        | Ok((_json, rest)) -> Ashes.IO.print("ok [" + rest + "]")
        | Error(error) -> Ashes.IO.print("error " + error)

try("[\"x\",[]]")
