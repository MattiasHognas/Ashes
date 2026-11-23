// expect: prefix|value 42 value ok
trait Label(a) =
    | text : a -> Str

implement Label(Int) =
    | text =
        given (value) -> Ashes.Text.fromInt(value)

implement Label(Str) =
    | text =
        given (value) -> value

let render : Str -> a -> Str requires {Label(a)} =
    given (prefix) ->
        given (x) -> prefix + Label.text(x)

let prefix _ =
    (let _ = Ashes.IO.write("prefix|")
    in "value ")

let decorated =
    Unit
    |> prefix
    |> render

Ashes.IO.print(decorated(42) + " " + decorated("ok"))
