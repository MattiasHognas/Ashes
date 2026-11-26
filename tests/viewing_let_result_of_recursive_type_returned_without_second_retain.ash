// expect: x
// A let owning a trimmed string returns a Result over a recursive type, whose Error holds a piece
// of that string: the result is normalized onto the reference-counted heap before the string is
// released, and the function returns that normalized value as its own, without retaining it again.
type Json =
    | JArray(Json, Json)
    | JArrayEnd

let recursive skipWs text =
    match Ashes.Text.unconsText(text) with
        | None -> ""
        | Some((h, t)) ->
            if h == " "
            then skipWs(t)
            else text

let parseValue text =
    (let trimmed = skipWs(text)
    in
        match Ashes.Text.unconsText(trimmed) with
            | None -> Error("end")
            | Some((h, rest)) ->
                if h == "["
                then Ok((JArrayEnd, rest))
                else Error(h))

match parseValue("x") with
    | Ok((_json, rest)) -> Ashes.IO.print(rest)
    | Error(error) -> Ashes.IO.print(error)
