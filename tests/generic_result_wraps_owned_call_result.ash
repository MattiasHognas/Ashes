// A function with a generic error type wraps an owned call result in `Ok`; a caller that knows the
// concrete type asks for the result and takes over a copy holding the owned value.
// expect: 20
type Collection =
    | items: List(Str)
    | count: Int

let make (n: Int) = Collection(items = ["a" + Ashes.Text.fromInt(n), "b"], count = n)

let prepend (extra: Str) (collection: Collection) =
    match collection with
        | Collection { items = items, count = count } -> Collection(items = extra :: items, count = count + 1)

let continueParsing (extra: Str) (tailResult: Result(e, Collection)) =
    match tailResult with
        | Error(error) -> Error(error)
        | Ok(collection) ->
            collection
            |> prepend(extra)
            |> Ok

let countOf (result: Result(Str, Collection)) =
    match result with
        | Error(_) -> 0
        | Ok(Collection { count = count }) -> count

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        loop(remaining - 1)(total + countOf(remaining
        |> make
        |> Ok
        |> continueParsing("x")))

0
|> loop(5)
|> Ashes.IO.print
