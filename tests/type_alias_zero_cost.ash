// expect: 42

type alias Identity(a) = a

type UserId = UserId(Identity(Int))

let unwrap id =
    match id with
        | UserId(value) -> value

UserId(42)
|> unwrap
|> Ashes.IO.print
