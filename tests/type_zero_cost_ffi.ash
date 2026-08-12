// expect: 5

type alias Text = Str

type Length = Length(Int)

external nativeLength(Text) -> Length = "strlen"

let unwrap length =
    match length with
        | Length(value) -> value

Ashes.IO.print(unwrap(nativeLength("Ashes")))
