// expect: Int|true Bool|false
trait Seed(a) =
    | first : Unit -> a
    | inspect : a -> Str

implement Seed(Int) =
    | first =
        given (_unit) -> 1
    | inspect =
        given (_value) -> "Int|"

implement Seed(Bool) =
    | first =
        given (_unit) -> true
    | inspect =
        given (_value) -> "Bool|"

let describe initial = Seed.inspect(initial)

let pick u x =
    (let initial = Seed.first(Unit)
    in
        let text = describe(initial)
        in
            let _ = Ashes.IO.write(text)
            in x == initial)

let a =
    Ashes.IO.write(Ashes.Trait.Show.show(pick(Unit)(1)) + " ")

false
|> pick(Unit)
|> Ashes.Trait.Show.show
|> Ashes.IO.print
