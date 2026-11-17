// expect-compile-error: Type mismatch:
let captured f =
    (let recursive apply x =
        if x == x
        then f(x)
        else f(x)
    in (apply(1), apply("text")))

captured(given (value) -> value)
