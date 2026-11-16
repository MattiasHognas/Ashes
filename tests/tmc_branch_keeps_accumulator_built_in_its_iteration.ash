// A loop with a plain tail self-call and a tail-modulo-cons self-call that conses onto an
// accumulator: the cons branch builds the accumulator's successor in the iteration's arena, so its
// back edge does not reset the arena even though the other branch passes the accumulator through.
// expect: a,b,c

let recursive keep (paths: List(Str)) (seen: List(Str)) =
    match paths with
        | [] -> []
        | path :: rest ->
            if Ashes.Collection.List.length(seen) > 5
            then keep(rest)(seen)
            else path :: keep(rest)(path :: seen)

[]
|> keep(["a", "b", "c"])
|> Ashes.Text.join(",")
|> Ashes.IO.print
