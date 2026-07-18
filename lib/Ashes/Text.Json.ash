type Json(B, N, F, S) =
    | JsonNull
    | JsonBool(B)
    | JsonInt(N)
    | JsonFloat(F)
    | JsonStr(S)
    | JsonArray(Json, Json)
    | JsonArrayEnd
    | JsonObject(S, Json, Json)
    | JsonObjectEnd

let recursive skipWs text =
    match Ashes.Text.uncons(text) with
        | None -> ""
        | Some((h, t)) ->
            if h == " "
            then skipWs(t)
            else
                if h == "\n"
                then skipWs(t)
                else
                    if h == "\r"
                    then skipWs(t)
                    else
                        if h == "\t"
                        then skipWs(t)
                        else text

let recursive consumeExact expected txt =
    match Ashes.Text.uncons(expected) with
        | None -> Ok(txt)
        | Some((want, wantRest)) ->
            match Ashes.Text.uncons(txt) with
                | None -> Error("unexpected end of input")
                | Some((got, gotRest)) ->
                    if got == want
                    then consumeExact(wantRest)(gotRest)
                    else Error("unexpected character: " + got)

let recursive parseStrBody acc text =
    match Ashes.Text.uncons(text) with
        | None -> Error("unterminated string")
        | Some((h, t)) ->
            if h == "\""
            then Ok((acc, t))
            else
                if h == "\\"
                then
                    match Ashes.Text.uncons(t) with
                        | None -> Error("unterminated escape")
                        | Some((esc, t2)) ->
                            if esc == "\""
                            then parseStrBody(acc + "\"")(t2)
                            else
                                if esc == "\\"
                                then parseStrBody(acc + "\\")(t2)
                                else
                                    if esc == "/"
                                    then parseStrBody(acc + "/")(t2)
                                    else
                                        if esc == "n"
                                        then parseStrBody(acc + "\n")(t2)
                                        else
                                            if esc == "r"
                                            then parseStrBody(acc + "\r")(t2)
                                            else
                                                if esc == "t"
                                                then parseStrBody(acc + "\t")(t2)
                                                else
                                                    if esc == "b"
                                                    then parseStrBody(acc + "\n")(t2)
                                                    else
                                                        if esc == "f"
                                                        then parseStrBody(acc + "\t")(t2)
                                                        else Error("unknown escape: \\" + esc)
                else parseStrBody(acc + h)(t)

let noEscapeString text =
    (let tb = Ashes.Byte.fromText(text)
    in
        let tlen = Ashes.Byte.length(tb)
        in
            let q = Ashes.Byte.indexOf(tb)(34)(0)
            in
                if q < 0
                then None
                else
                    let bs = Ashes.Byte.indexOf(tb)(92)(0)
                    in
                        if bs >= 0
                        then
                            if bs < q
                            then None
                            else Some((Ashes.Byte.subText(tb)(0)(q), Ashes.Byte.subView(tb)(q + 1)(tlen - q - 1)))
                        else Some((Ashes.Byte.subText(tb)(0)(q), Ashes.Byte.subView(tb)(q + 1)(tlen - q - 1))))

let parseQuotedStr text =
    match Ashes.Text.uncons(text) with
        | None -> Error("expected '\"'")
        | Some((h, t)) ->
            if h == "\""
            then
                match noEscapeString(t) with
                    | Some(result) -> Ok(result)
                    | None -> parseStrBody("")(t)
            else Error("expected '\"' to open string")

let recursive takeNum acc text =
    match Ashes.Text.uncons(text) with
        | None -> (acc, "")
        | Some((h, t)) ->
            match h with
                | "0" -> takeNum(acc + h)(t)
                | "1" -> takeNum(acc + h)(t)
                | "2" -> takeNum(acc + h)(t)
                | "3" -> takeNum(acc + h)(t)
                | "4" -> takeNum(acc + h)(t)
                | "5" -> takeNum(acc + h)(t)
                | "6" -> takeNum(acc + h)(t)
                | "7" -> takeNum(acc + h)(t)
                | "8" -> takeNum(acc + h)(t)
                | "9" -> takeNum(acc + h)(t)
                | "-" -> takeNum(acc + h)(t)
                | "+" -> takeNum(acc + h)(t)
                | "." -> takeNum(acc + h)(t)
                | "e" -> takeNum(acc + h)(t)
                | "E" -> takeNum(acc + h)(t)
                | _ -> (acc, text)

let recursive hasFloatMark text =
    match Ashes.Text.uncons(text) with
        | None -> false
        | Some((h, t)) ->
            if h == "."
            then true
            else
                if h == "e"
                then true
                else
                    if h == "E"
                    then true
                    else hasFloatMark(t)

let recursive parseValue text =
    (let trimmed = skipWs(text)
    in
        match Ashes.Text.uncons(trimmed) with
            | None -> Error("unexpected end of input")
            | Some((h, rest)) ->
                if h == "n"
                then
                    match consumeExact("null")(trimmed) with
                        | Ok(after) -> Ok((JsonNull, after))
                        | Error(e) -> Error(e)
                else
                    if h == "t"
                    then
                        match consumeExact("true")(trimmed) with
                            | Ok(after) -> Ok((JsonBool(true), after))
                            | Error(e) -> Error(e)
                    else
                        if h == "f"
                        then
                            match consumeExact("false")(trimmed) with
                                | Ok(after) -> Ok((JsonBool(false), after))
                                | Error(e) -> Error(e)
                        else
                            if h == "\""
                            then
                                match parseQuotedStr(trimmed) with
                                    | Error(e) -> Error(e)
                                    | Ok((s, after)) -> Ok((JsonStr(s), after))
                            else
                                if h == "["
                                then
                                    let recursive parseArr cur =
                                        let tc = skipWs(cur)
                                        in
                                            match Ashes.Text.uncons(tc) with
                                                | None -> Error("unterminated array")
                                                | Some((c, ctail)) ->
                                                    if c == "]"
                                                    then Ok((JsonArrayEnd, ctail))
                                                    else
                                                        match parseValue(tc) with
                                                            | Error(e) -> Error(e)
                                                            | Ok((elem, afterElem)) ->
                                                                let ta = skipWs(afterElem)
                                                                in
                                                                    match Ashes.Text.uncons(ta) with
                                                                        | None -> Error("unterminated array")
                                                                        | Some((sep, sepTail)) ->
                                                                            if sep == "]"
                                                                            then Ok((JsonArray(elem)(JsonArrayEnd), sepTail))
                                                                            else
                                                                                if sep == ","
                                                                                then
                                                                                    match parseArr(sepTail) with
                                                                                        | Error(e) -> Error(e)
                                                                                        | Ok((rArr, rAfter)) -> Ok((JsonArray(elem)(rArr), rAfter))
                                                                                else Error("expected ',' or ']' in array")
                                    in parseArr(rest)
                                else
                                    if h == "{"
                                    then
                                        let recursive parseObj cur =
                                            let tc = skipWs(cur)
                                            in
                                                match Ashes.Text.uncons(tc) with
                                                    | None -> Error("unterminated object")
                                                    | Some((c, ctail)) ->
                                                        if c == "}"
                                                        then Ok((JsonObjectEnd, ctail))
                                                        else
                                                            if c == "\""
                                                            then
                                                                match parseQuotedStr(tc) with
                                                                    | Error(e) -> Error(e)
                                                                    | Ok((key, afterKey)) ->
                                                                        let tak = skipWs(afterKey)
                                                                        in
                                                                            match Ashes.Text.uncons(tak) with
                                                                                | None -> Error("expected ':' after key")
                                                                                | Some((colon, afterColon)) ->
                                                                                    if colon == ":"
                                                                                    then
                                                                                        match parseValue(afterColon) with
                                                                                            | Error(e) -> Error(e)
                                                                                            | Ok((v, afterVal)) ->
                                                                                                let tav = skipWs(afterVal)
                                                                                                in
                                                                                                    match Ashes.Text.uncons(tav) with
                                                                                                        | None -> Error("unterminated object")
                                                                                                        | Some((sep, sepTail)) ->
                                                                                                            if sep == "}"
                                                                                                            then Ok((JsonObject(key)(v)(JsonObjectEnd), sepTail))
                                                                                                            else
                                                                                                                if sep == ","
                                                                                                                then
                                                                                                                    match parseObj(sepTail) with
                                                                                                                        | Error(e) -> Error(e)
                                                                                                                        | Ok((rObj, rAfter)) -> Ok((JsonObject(key)(v)(rObj), rAfter))
                                                                                                                else Error("expected ',' or '}' in object")
                                                                                    else Error("expected ':' after key")
                                                            else Error("expected '\"' for key or '}'")
                                        in parseObj(rest)
                                    else
                                        let numAndRest = takeNum("")(trimmed)
                                        in
                                            match numAndRest with
                                                | (numStr, numRest) ->
                                                    if numStr == ""
                                                    then Error("unexpected character: " + h)
                                                    else
                                                        if hasFloatMark(numStr)
                                                        then
                                                            match Ashes.Text.parseFloat(numStr) with
                                                                | Ok(f) -> Ok((JsonFloat(f), numRest))
                                                                | Error(e) -> Error(e)
                                                        else
                                                            match Ashes.Text.parseInt(numStr) with
                                                                | Ok(n) -> Ok((JsonInt(n), numRest))
                                                                | Error(e) -> Error(e))

let parse text =
    match parseValue(text) with
        | Error(e) -> Error(e)
        | Ok((value, rest)) ->
            if skipWs(rest) == ""
            then Ok(value)
            else Error("trailing input after JSON value")

let recursive escStr acc text =
    match Ashes.Text.uncons(text) with
        | None -> acc
        | Some((h, t)) ->
            if h == "\""
            then escStr(acc + "\\\"")(t)
            else
                if h == "\\"
                then escStr(acc + "\\\\")(t)
                else
                    if h == "\n"
                    then escStr(acc + "\\n")(t)
                    else
                        if h == "\r"
                        then escStr(acc + "\\r")(t)
                        else
                            if h == "\t"
                            then escStr(acc + "\\t")(t)
                            else escStr(acc + h)(t)

let recursive stringify json =
    (let recursive strArr elem rest =
        let s = stringify(elem)
        in
            match rest with
                | JsonArrayEnd -> s
                | JsonArray(ne, nr) -> s + "," + strArr(ne)(nr)
                | _ -> s
    in
        let recursive strObj rest =
            match rest with
                | JsonObjectEnd -> ""
                | JsonObject(k, v, nr) -> ",\"" + escStr("")(k) + "\":" + stringify(v) + strObj(nr)
                | _ -> ""
        in
            match json with
                | JsonNull -> "null"
                | JsonBool(b) ->
                    if b
                    then "true"
                    else "false"
                | JsonInt(n) -> Ashes.Text.fromInt(n)
                | JsonFloat(f) -> Ashes.Text.fromFloat(f)
                | JsonStr(s) -> "\"" + escStr("")(s) + "\""
                | JsonArrayEnd -> "[]"
                | JsonArray(elem, rest) -> "[" + strArr(elem)(rest) + "]"
                | JsonObjectEnd -> "{}"
                | JsonObject(key, v, rest) -> "{\"" + escStr("")(key) + "\":" + stringify(v) + strObj(rest) + "}")

let get key json =
    (let recursive go cur =
        match cur with
            | JsonObjectEnd -> Error("key not found: " + key)
            | JsonObject(k, v, rest) ->
                if k == key
                then Ok(v)
                else go(rest)
            | _ -> Error("not a JSON object")
    in go(json))

let asStr json =
    match json with
        | JsonStr(s) -> Ok(s)
        | _ -> Error("not a JSON string")

let asInt json =
    match json with
        | JsonInt(n) -> Ok(n)
        | _ -> Error("not a JSON integer")

let asFloat json =
    match json with
        | JsonFloat(f) -> Ok(f)
        | _ -> Error("not a JSON float")

let asBool json =
    match json with
        | JsonBool(b) -> Ok(b)
        | _ -> Error("not a JSON boolean")

let isNull json =
    match json with
        | JsonNull -> true
        | _ -> false

let index i json =
    (let recursive go cur idx =
        match cur with
            | JsonArrayEnd -> Error("JSON array index out of bounds")
            | JsonArray(elem, rest) ->
                if idx <= 0
                then Ok(elem)
                else go(rest)(idx - 1)
            | _ -> Error("not a JSON array")
    in go(json)(i))
