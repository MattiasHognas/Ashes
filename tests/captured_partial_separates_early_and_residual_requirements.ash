// expect: scope:7|bound|scope:true scope:17
trait Describe(a) =
    | describe : a -> Str

implement Describe(Int) =
    | describe =
        given (value) -> Ashes.Text.fromInt(value)

let scenario captured =
    (let render first =
        let _ = Ashes.IO.write(captured + Describe.describe(first) + "|")
        in
            given (last) -> captured + Ashes.Trait.Show.show(last)
    in
        let alias = render
        in
            let invoke _ =
                let captured = "shadow:"
                in
                    let partial = alias(7)
                    in
                        let _ = Ashes.IO.write("bound|")
                        in partial(true) + " " + partial(17)
            in invoke(Unit))

"scope:"
|> scenario
|> Ashes.IO.print
