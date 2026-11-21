// expect: true true
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
    (let _ = initial |> Seed.inspect
    in
        given (x) -> x == initial)

let pick =
    given (_unit) ->
        Unit
        |> Seed.first
        |> around

let choose : (Unit -> a -> Bool, Unit -> a -> Bool) -> a -> Bool =
    given (pair) ->
        match pair with
            | (unused, selected) -> selected(Unit)

let active =
    given (u) ->
        given (x) -> true

let check = choose((pick, active))

Ashes.IO.print(Ashes.Trait.Show.show(check(1)) + " " + Ashes.Trait.Show.show(check(true)))
