// expect-compile-error: is missing operation
capability KV =
    | get : Str -> Int
    | put : Str -> Int -> Int

provide KV =
    | get =
        given (_) -> 0

Ashes.IO.print(0)
