// expect: 42

type alias Identity(a) = a

type UserId = UserId(Identity(Int))

let unwrap id =
    match id with
        | UserId(value) -> value

Ashes.IO.print(unwrap(UserId(42)))
