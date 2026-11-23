// expect: Int|true Bool|true
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

let check x =
    (let initial = Seed.first(Unit)
    in
        let _ =
            initial
            |> Seed.inspect
            |> Ashes.IO.write
        in x == initial)

let _ =
    Ashes.IO.write(Ashes.Trait.Show.show(check(1)) + " ")

true
|> check
|> Ashes.Trait.Show.show
|> Ashes.IO.print
