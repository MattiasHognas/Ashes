// expect: yz 5
// A `let` binds the uncons of a string another `let` owns, matched later: the matched tail views
// the owned string, so the string is released only after the tail is copied out. And a loop
// passes an uncons of a `let`-bound fresh string to its back edge: the parameter holds the view.
let id (text: Str) = text + ""

let restOf text =
    (let s = id(text)
    in
        let parts = Ashes.Text.unconsText(s)
        in
            match parts with
                | None -> "end"
                | Some((_h, rest)) -> rest)

let recursive count (parts: Maybe((Str, Str))) (n: Int) =
    match parts with
        | None -> n
        | Some((_h, rest)) ->
            let s = id(rest)
            in
                count(Ashes.Text.unconsText(s))(n + 1)

Ashes.IO.print(restOf("xyz") + " " + Ashes.Text.fromInt(count(Ashes.Text.unconsText("hello"))(0)))
