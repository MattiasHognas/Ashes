// expect: entered|bound|v=42 v=true
let render =
    given (prefix) ->
        let _ = Ashes.IO.write("entered|")
        in
            given (value) -> prefix + Ashes.Trait.Show.show(value)

let partial = render("v=")

let marker = Ashes.IO.write("bound|")

Ashes.IO.print(partial(42) + " " + partial(true))
