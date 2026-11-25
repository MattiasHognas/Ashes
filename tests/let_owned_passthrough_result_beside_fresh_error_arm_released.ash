// expect: 5
// A match arm whose value comes out of a let owning a parameter-returning call's result (a view
// handed back), beside an arm building a fresh Error: both arms hand the join a reference of their
// own, so the function returns the join as it is, without retaining it again.
type Json =
    | JStr(Str)
    | JArray(Json, Json)
    | JArrayEnd

let keepText (text: Str) = text

let produce (n: Int) =
    if n > 0
    then Ok((JStr("a" + Ashes.Text.fromInt(n)), "]" + Ashes.Text.fromInt(n)))
    else Error("none")

let step (n: Int) =
    match produce(n) with
        | Error(e) -> Error(e)
        | Ok((elem, afterElem)) ->
            let ta = keepText(afterElem)
            in
                match Ashes.Text.unconsText(ta) with
                    | None -> Error("unterminated array")
                    | Some((sep, _sepTail)) ->
                        if sep == "]"
                        then Ok((JArrayEnd, ""))
                        else Error("bad")

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        match step(n) with
            | Ok((_j, _r)) -> loop(n - 1)(acc + 1)
            | Error(_e) -> loop(n - 1)(acc)

0
|> loop(5)
|> Ashes.Text.fromInt
|> Ashes.IO.print
