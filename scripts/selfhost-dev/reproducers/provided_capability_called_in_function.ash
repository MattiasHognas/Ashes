capability Shift =
    | transform : Int -> Int

provide Shift =
    | transform =
        given (value: Int) -> value + 4

let apply (n: Int) = Shift.transform(n)

28
|> apply
|> Ashes.IO.print
