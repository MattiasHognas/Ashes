// A user's own foldLeft/map, unrelated to Ashes.Collection.List's, must run as written — the fusion
// is gated on resolved callee identity (the same ResolveCalleeQualifiedName every stdlib call
// already resolves through), not on the literal names `foldLeft`/`map`.
// expect: 12
let recursive foldLeft f init xs =
    match xs with
        | [] -> init
        | head :: tail -> foldLeft(f)(f(init)(head) + 1)(tail)

let recursive map g xs =
    match xs with
        | [] -> []
        | head :: tail -> g(head) :: map(g)(tail)

let add a b = a + b

[1, 2, 3]
|> map(given (x) -> x + 1)
|> foldLeft(add)(0)
|> Ashes.Text.fromInt
|> Ashes.IO.print
