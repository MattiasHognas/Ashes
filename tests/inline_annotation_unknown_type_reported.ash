// expect-compile-error: Unknown type name 'Foo'

let recursive g (items: List(Foo)) (acc: List(Foo)) =
    match items with
        | [] -> acc
        | head :: rest -> g(rest)(head :: acc)

Ashes.IO.print(1)
