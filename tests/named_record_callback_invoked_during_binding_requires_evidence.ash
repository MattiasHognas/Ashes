// expect-compile-error: is needed while evaluating this binding
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

type Holder(a) =
    | callback: Unit -> a

let invoke (item: Holder(a)) = item.callback(Unit)

let args = Holder(callback = pick)

let check = invoke(args)

let marker = Ashes.IO.write("bound|")

Ashes.IO.print(Ashes.Trait.Show.show(check(1)) + " " + Ashes.Trait.Show.show(check(true)))
