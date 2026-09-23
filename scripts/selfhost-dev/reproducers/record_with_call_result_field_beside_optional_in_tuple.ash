type Kind =
    | Word
    | Let

type Token =
    | kind: Kind
    | position: Int

let makeToken (kind: Kind) (position: Int) = Token(kind = kind, position = position)

let keywordKind (n: Int) =
    match n with
        | 3 -> Let
        | _ -> Word

let readNext (position: Int) = (Token(kind = keywordKind(position), position = position), Some(position))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match readNext(n) with
            | (Token { position = position }, _note) -> rounds(n - 1)(total + position)

0
|> rounds(ROUNDS)
|> Ashes.IO.print
