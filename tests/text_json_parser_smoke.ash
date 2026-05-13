// expect: ok
let isWhitespace = 
    fun (head) -> 
        if head == " "
        then true
        else 
            if head == "\n"
            then true
            else 
                if head == "\t"
                then true
                else 
                    if head == "\r"
                    then true
                    else false
in 
    let rec skipWhitespace text = 
        match Ashes.Text.uncons(text) with
            | None -> ""
            | Some((head, tail)) -> 
                if isWhitespace(head)
                then skipWhitespace(tail)
                else text
    in 
        let isDigit = 
            fun (head) -> 
                match Ashes.Text.parseInt(head) with
                    | Ok(_) -> true
                    | Error(_) -> false
        in 
            let rec consumeExact expected text = 
                match Ashes.Text.uncons(expected) with
                    | None -> Ok(text)
                    | Some((wanted, wantedRest)) -> 
                        match Ashes.Text.uncons(text) with
                            | None -> Error("unexpected end of input")
                            | Some((actual, actualRest)) -> 
                                if actual == wanted
                                then consumeExact(wantedRest)(actualRest)
                                else Error("unexpected token")
            in 
                let rec parseStringBody acc text = 
                    match Ashes.Text.uncons(text) with
                        | None -> Error("unterminated string")
                        | Some((head, tail)) -> 
                            if head == "\""
                            then Ok((acc, tail))
                            else 
                                if head == "\\"
                                then Error("string escapes not supported in this demo")
                                else parseStringBody(acc + head)(tail)
                in 
                    let parseString = 
                        fun (text) -> 
                            match Ashes.Text.uncons(text) with
                                | None -> Error("expected string")
                                | Some((head, tail)) -> 
                                    if head == "\""
                                    then parseStringBody("")(tail)
                                    else Error("expected string")
                    in 
                        let rec takeNumberToken acc text = 
                            match Ashes.Text.uncons(text) with
                                | None -> (acc, "")
                                | Some((head, tail)) -> 
                                    match Ashes.Text.parseInt(head) with
                                        | Ok(_) -> takeNumberToken(acc + head)(tail)
                                        | Error(_) -> 
                                            if head == "-"
                                            then takeNumberToken(acc + head)(tail)
                                            else 
                                                if head == "+"
                                                then takeNumberToken(acc + head)(tail)
                                                else 
                                                    if head == "."
                                                    then takeNumberToken(acc + head)(tail)
                                                    else 
                                                        if head == "e"
                                                        then takeNumberToken(acc + head)(tail)
                                                        else 
                                                            if head == "E"
                                                            then takeNumberToken(acc + head)(tail)
                                                            else (acc, text)
                        in 
                            let rec containsFloatMarker text = 
                                match Ashes.Text.uncons(text) with
                                    | None -> false
                                    | Some((head, tail)) -> 
                                        if head == "."
                                        then true
                                        else 
                                            if head == "e"
                                            then true
                                            else 
                                                if head == "E"
                                                then true
                                                else containsFloatMarker(tail)
                            in 
                                let rec parseValue text = 
                                    let trimmed = skipWhitespace(text)
                                    in 
                                        let rec parseArrayItems current acc hasValues = 
                                            let currentTrimmed = skipWhitespace(current)
                                            in 
                                                match Ashes.Text.uncons(currentTrimmed) with
                                                    | None -> Error("unterminated array")
                                                    | Some((head, tail)) -> 
                                                        if head == "]"
                                                        then Ok(("Array(" + acc + ")", tail))
                                                        else 
                                                            match parseValue(currentTrimmed) with
                                                                | Error(message) -> Error(message)
                                                                | Ok((value, afterValue)) -> 
                                                                    let nextAcc = 
                                                                        if hasValues
                                                                        then acc + ", " + value
                                                                        else value
                                                                    in 
                                                                        let afterValueTrimmed = skipWhitespace(afterValue)
                                                                        in 
                                                                            match Ashes.Text.uncons(afterValueTrimmed) with
                                                                                | None -> Error("unterminated array")
                                                                                | Some((separator, separatorTail)) -> 
                                                                                    if separator == ","
                                                                                    then parseArrayItems(separatorTail)(nextAcc)(true)
                                                                                    else 
                                                                                        if separator == "]"
                                                                                        then Ok(("Array(" + nextAcc + ")", separatorTail))
                                                                                        else Error("expected , or ]")
                                        in 
                                            let rec parseObjectMembers current acc hasValues = 
                                                let currentTrimmed = skipWhitespace(current)
                                                in 
                                                    let rec parseKeyBody keyAcc keyText = 
                                                        match Ashes.Text.uncons(keyText) with
                                                            | None -> Error("unterminated string")
                                                            | Some((keyHead, keyTail)) -> 
                                                                if keyHead == "\""
                                                                then Ok((keyAcc, keyTail))
                                                                else 
                                                                    if keyHead == "\\"
                                                                    then Error("string escapes not supported in this demo")
                                                                    else parseKeyBody(keyAcc + keyHead)(keyTail)
                                                    in 
                                                        match Ashes.Text.uncons(currentTrimmed) with
                                                            | None -> Error("unterminated object")
                                                            | Some((head, tail)) -> 
                                                                if head == "}"
                                                                then Ok(("Object(" + acc + ")", tail))
                                                                else 
                                                                    if head == "\""
                                                                    then 
                                                                        match parseKeyBody("")(tail) with
                                                                            | Error(message) -> Error(message)
                                                                            | Ok((key, afterKey)) -> 
                                                                                let afterKeyTrimmed = skipWhitespace(afterKey)
                                                                                in 
                                                                                    match Ashes.Text.uncons(afterKeyTrimmed) with
                                                                                        | None -> Error("unexpected end of input")
                                                                                        | Some((separator, afterColon)) -> 
                                                                                            if separator == ":"
                                                                                            then 
                                                                                                match parseValue(afterColon) with
                                                                                                    | Error(message) -> Error(message)
                                                                                                    | Ok((value, afterValue)) -> 
                                                                                                        let nextAcc = 
                                                                                                            if hasValues
                                                                                                            then acc + ", " + key + "=" + value
                                                                                                            else key + "=" + value
                                                                                                        in 
                                                                                                            let afterValueTrimmed = skipWhitespace(afterValue)
                                                                                                            in 
                                                                                                                match Ashes.Text.uncons(afterValueTrimmed) with
                                                                                                                    | None -> Error("unterminated object")
                                                                                                                    | Some((afterSeparator, separatorTail)) -> 
                                                                                                                        if afterSeparator == ","
                                                                                                                        then parseObjectMembers(separatorTail)(nextAcc)(true)
                                                                                                                        else 
                                                                                                                            if afterSeparator == "}"
                                                                                                                            then Ok(("Object(" + nextAcc + ")", separatorTail))
                                                                                                                            else Error("expected , or }")
                                                                                            else Error("expected :")
                                                                    else Error("expected string")
                                            in 
                                                match Ashes.Text.uncons(trimmed) with
                                                    | None -> Error("expected JSON value")
                                                    | Some((head, tail)) -> 
                                                        if head == "\""
                                                        then 
                                                            match parseString(trimmed) with
                                                                | Ok((value, rest)) -> Ok(("Str(" + value + ")", rest))
                                                                | Error(message) -> Error(message)
                                                        else 
                                                            if head == "["
                                                            then parseArrayItems(tail)("")(false)
                                                            else 
                                                                if head == "{"
                                                                then parseObjectMembers(tail)("")(false)
                                                                else 
                                                                    if head == "t"
                                                                    then 
                                                                        match consumeExact("true")(trimmed) with
                                                                            | Ok(rest) -> Ok(("True", rest))
                                                                            | Error(message) -> Error(message)
                                                                    else 
                                                                        if head == "f"
                                                                        then 
                                                                            match consumeExact("false")(trimmed) with
                                                                                | Ok(rest) -> Ok(("False", rest))
                                                                                | Error(message) -> Error(message)
                                                                        else 
                                                                            if head == "n"
                                                                            then 
                                                                                match consumeExact("null")(trimmed) with
                                                                                    | Ok(rest) -> Ok(("Null", rest))
                                                                                    | Error(message) -> Error(message)
                                                                            else 
                                                                                if head == "-"
                                                                                then 
                                                                                    let tokenAndRest = takeNumberToken("")(trimmed)
                                                                                    in 
                                                                                        match tokenAndRest with
                                                                                            | (token, rest) -> 
                                                                                                if containsFloatMarker(token)
                                                                                                then 
                                                                                                    match Ashes.Text.parseFloat(token) with
                                                                                                        | Ok(_) -> Ok(("Float(" + token + ")", rest))
                                                                                                        | Error(message) -> Error(message)
                                                                                                else 
                                                                                                    match Ashes.Text.parseInt(token) with
                                                                                                        | Ok(_) -> Ok(("Int(" + token + ")", rest))
                                                                                                        | Error(message) -> Error(message)
                                                                                else 
                                                                                    if isDigit(head)
                                                                                    then 
                                                                                        let tokenAndRest = takeNumberToken("")(trimmed)
                                                                                        in 
                                                                                            match tokenAndRest with
                                                                                                | (token, rest) -> 
                                                                                                    if containsFloatMarker(token)
                                                                                                    then 
                                                                                                        match Ashes.Text.parseFloat(token) with
                                                                                                            | Ok(_) -> Ok(("Float(" + token + ")", rest))
                                                                                                            | Error(message) -> Error(message)
                                                                                                    else 
                                                                                                        match Ashes.Text.parseInt(token) with
                                                                                                            | Ok(_) -> Ok(("Int(" + token + ")", rest))
                                                                                                            | Error(message) -> Error(message)
                                                                                    else Error("expected JSON value")
                                in 
                                    let parseDocument = 
                                        fun (text) -> 
                                            match parseValue(text) with
                                                | Error(message) -> Error(message)
                                                | Ok((value, rest)) -> 
                                                    if skipWhitespace(rest) == ""
                                                    then Ok(value)
                                                    else Error("trailing input")
                                    in 
                                        let sample = " { \"name\" : \"Ashes\", \"active\" : true, \"count\" : 42, \"ratio\" : 1.5, \"items\" : [ null, false, { \"nested\" : \"ok\" } ] } "
                                        in 
                                            let expected = "Object(name=Str(Ashes), active=True, count=Int(42), ratio=Float(1.5), items=Array(Null, False, Object(nested=Str(ok))))"
                                            in 
                                                match parseDocument(sample) with
                                                    | Error(message) -> Ashes.IO.print(message)
                                                    | Ok(summary) -> 
                                                        if summary == expected
                                                        then Ashes.IO.print("ok")
                                                        else Ashes.IO.print(summary)
