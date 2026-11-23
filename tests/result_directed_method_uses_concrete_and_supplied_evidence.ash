// expect: 17/17/yes
trait Seed(a) =
    | first : Unit -> a

implement Seed(Int) =
    | first =
        given (_u) -> 17

implement Seed(Bool) =
    | first =
        given (_u) -> true

let render : (a -> Str) -> Str requires {Seed(a)} =
    given (showValue) ->
        let direct : Int = Seed.first(Unit)
        in
            let concreteMaker : Unit -> Int = Seed.first
            in
                let label =
                    Unit
                    |> Seed.first
                    |> showValue
                in
                    Ashes.Text.fromInt(direct) + "/" + Ashes.Text.fromInt(concreteMaker(Unit)) + "/" + label

let boolean (b: Bool) =
    if b
    then "yes"
    else "no"

boolean
|> render
|> Ashes.IO.print
