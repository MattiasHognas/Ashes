// A function with a generic error type passes an owned value to a call of the contract's types and
// returns that call's result: the result holds references of its own, so the owned value is
// released at the return.
// expect: 20
type Collection =
    | items: List(Str)
    | count: Int

type Loaded =
    | names: List(Str)
    | total: Int

let make (n: Int) = Collection(items = ["a" + Ashes.Text.fromInt(n), "b"], count = n)

let prepend (extra: Str) (collection: Collection) =
    match collection with
        | Collection { items = items, count = count } -> Collection(items = extra :: items, count = count + 1)

let complete (collection: Collection) =
    match collection with
        | Collection { items = items, count = count } ->
            Loaded(names = items, total = count)
            |> Ashes.Internal.deepCopy
            |> Ok

let finish (collection: Collection) (result: Result(e, Str)) =
    match result with
        | Error(error) -> Error(error)
        | Ok(outer) ->
            collection
            |> prepend(outer)
            |> complete

let totalOf (result: Result(Str, Loaded)) =
    match result with
        | Error(_) -> 0
        | Ok(Loaded { total = total }) -> total

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        loop(remaining - 1)(total + totalOf(finish(make(remaining))(Ok("x"))))

0
|> loop(5)
|> Ashes.IO.print
