let recursive skip (items: List(Str)) (count: Int) =
    match items with
        | "=" :: _ -> (items, count)
        | _token :: tail -> skip(tail)(count + 1)
        | [] -> (items, count)

let run (seed: Int) =
    match skip(["smoke", "coal", "smoke", "=", "ash"])(seed) with
        | (rest, _count) -> Ashes.Collection.List.length(rest)

let recursive loop (remaining: Int) (checksum: Int) =
    if remaining == 0
    then checksum
    else loop(remaining - 1)(checksum + run(2))

0
|> loop(ROUNDS)
|> Ashes.IO.print
