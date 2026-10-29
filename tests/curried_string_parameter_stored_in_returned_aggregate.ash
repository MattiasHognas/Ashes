// expect: 12

type Kind =
    | KindA
    | KindB

type Token =
    | kind: Kind
    | text: Str

let pair (k: Kind) (t: Str) = (k, t)

let token (k: Kind) (t: Str) = Token(kind = k, text = t)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let text = "word" + Ashes.Text.fromInt(n)
        in rounds(n - 1)(total + Ashes.Collection.List.length([pair(KindA)(text), pair(KindB)("b")]) + Ashes.Collection.List.length([token(KindA)(text), token(KindB)("b")]))

0
|> rounds(3)
|> Ashes.IO.print
