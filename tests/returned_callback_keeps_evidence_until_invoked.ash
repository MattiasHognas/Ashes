// expect: bound|Int|true Bool|true
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

let around initial =
    (let _ =
        initial
        |> Seed.inspect
        |> Ashes.IO.write
    in
        given (x) -> x == initial)

let pick =
    given (_unit) ->
        Unit
        |> Seed.first
        |> around

let keep f = f

let delayed = keep(pick)

let marker = Ashes.IO.write("bound|")

let first : Int -> Bool = delayed(Unit)

let a =
    Ashes.IO.write(Ashes.Trait.Show.show(first(1)) + " ")

let second : Bool -> Bool = delayed(Unit)

true
|> second
|> Ashes.Trait.Show.show
|> Ashes.IO.print
