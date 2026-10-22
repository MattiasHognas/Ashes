// A function with a generic error type returns an owned call result as it is in its `Ok` arm; a
// caller that knows the concrete type asks for it and takes it over, directly and through a
// function returning the first one's result as it is.
// expect: 30
type Collection =
    | items: List(Str)
    | count: Int

type Loaded =
    | names: List(Str)
    | total: Int

let make (n: Int) = Collection(items = ["a" + Ashes.Text.fromInt(n), "b"], count = n)

let complete (collection: Collection) =
    match collection with
        | Collection { items = items, count = count } ->
            Loaded(names = items, total = count)
            |> Ashes.Internal.deepCopy
            |> Ok

let finish (collection: Collection) (result: Result(e, Str)) =
    match result with
        | Error(error) -> Error(error)
        | Ok(_outer) -> complete(collection)

let finishAll (collection: Collection) (result: Result(e, Str)) = finish(collection)(result)

let totalOf (result: Result(Str, Loaded)) =
    match result with
        | Error(_) -> 0
        | Ok(Loaded { total = total }) -> total

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        loop(remaining - 1)(total + totalOf(finish(make(remaining))(Ok("x"))) + totalOf(finishAll(make(remaining))(Ok("y"))))

0
|> loop(5)
|> Ashes.IO.print
