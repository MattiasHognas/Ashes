// expect: xy yz a|bc 5 5 bcd
// A newly produced string handed straight to a builtin whose result views it (the uncons family,
// a byte range, a text's bytes) is owned by the scope consuming the builtin's result, as a `let`
// binding of it would be: released behind the match, the let body or the call that reads the
// views, and around a loop's back edge once the next iteration's view is materialized.
let id (text: Str) = text + ""

let first text =
    match text
    |> id
    |> Ashes.Text.unconsText with
        | None -> "end"
        | Some((h, rest)) -> h + rest

let restOf text =
    (let parts =
        text
        |> id
        |> Ashes.Text.unconsText
    in
        match parts with
            | None -> "end"
            | Some((_h, rest)) -> rest)

let describe (parts: Maybe((Str, Str))) =
    match parts with
        | None -> "end"
        | Some((h, rest)) -> h + "|" + rest

let recursive count (parts: Maybe((Str, Str))) (n: Int) =
    match parts with
        | None -> n
        | Some((_h, rest)) ->
            count(rest
            |> id
            |> Ashes.Text.unconsText)(n + 1)

let recursive walk (text: Str) (n: Int) =
    match text
    |> id
    |> Ashes.Text.unconsText with
        | None -> n
        | Some((_h, rest)) -> walk(rest)(n + 1)

let tailOf (text: Str) =
    Ashes.Byte.subView(Ashes.Byte.fromText(text + "!"))(1)(3)

Ashes.IO.print(first("xy") + " " + restOf("xyz") + " " + describe("abc"
|> id
|> Ashes.Text.unconsText) + " " + Ashes.Text.fromInt(count(Ashes.Text.unconsText("hello"))(0)) + " " + Ashes.Text.fromInt(walk("hello")(0)) + " " + tailOf("abcd"))
