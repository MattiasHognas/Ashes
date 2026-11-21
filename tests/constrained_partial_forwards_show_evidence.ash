// expect: n=42 n=true
let render : Str -> a -> Str requires {Show(a)} =
    given (prefix) ->
        given (value) -> prefix + Ashes.Trait.Show.show(value)

let describe = render("n=")

Ashes.IO.print(describe(42) + " " + describe(true))
