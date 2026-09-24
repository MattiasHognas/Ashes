// expect: 500500
// A nullary constructor a function returns, stored in a record by a curried helper, is one shared
// immortal cell: nothing is allocated per call, so nothing is left behind when the record goes.
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

let readNext (position: Int) =
    makeToken(keywordKind(position))(position)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match readNext(n) with
            | Token { position = position } -> rounds(n - 1)(total + position)

0
|> rounds(1000)
|> Ashes.IO.print
