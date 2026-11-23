// expect: 7|7 true
trait Describe(a) =
    | describe : a -> Str

implement Describe(Int) =
    | describe =
        given (value) -> Ashes.Text.fromInt(value)

let render first =
    (let prefix = Describe.describe(first)
    in
        let _ = Ashes.IO.write(prefix + "|")
        in
            given (last) -> prefix + " " + Ashes.Trait.Show.show(last))

let use first =
    (let partial = render(first)
    in partial(true))

7
|> use
|> Ashes.IO.print
