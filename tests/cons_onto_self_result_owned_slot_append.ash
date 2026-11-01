// expect: 80
type Tok =
    | kind: Int
    | text: Str

let recursive appendToks (front: List(Tok)) (back: List(Tok)) =
    match front with
        | [] -> back
        | token :: rest -> token :: appendToks(rest)(back)

let recursive toks (n: Int) =
    if n == 0
    then []
    else Tok(kind = n, text = "t" + Ashes.Text.fromInt(n)) :: toks(n - 1)

let recursive total (xs: List(Tok)) =
    match xs with
        | [] -> 0
        | Tok { kind = k, text = t } :: rest -> k + Ashes.Text.byteLength(t) + total(rest)

let run (seed: Int) =
    (let a = toks(seed % 3 + 1)
    in
        let b = toks(2)
        in
            let c = appendToks(a)(b)
            in
                total(c) + total(b) + total(appendToks([])(b)) + total(a))

let recursive rounds (n: Int) (sum: Int) =
    if n == 0
    then sum
    else rounds(n - 1)(sum + run(n))

0
|> rounds(2)
|> Ashes.IO.print
