// A function returns its contract-typed parameter as it is: its entry adopts the owned call result
// the caller hands over, and its return hands that reference on rather than retaining it again.
// expect: 15
type Collection =
    | items: List(Str)
    | count: Int

let make (n: Int) = Collection(items = ["a" + Ashes.Text.fromInt(n), "b"], count = n)

let wrap (n: Int) =
    n
    |> make
    |> Ok

let pinned (result: Result(Str, Collection)) = result

let countOf (result: Result(Str, Collection)) =
    match result with
        | Error(_) -> 0
        | Ok(Collection { count = count }) -> count

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        loop(remaining - 1)(total + countOf(remaining
        |> wrap
        |> pinned))

0
|> loop(5)
|> Ashes.IO.print
