// expect: 3000
let produce (k: Int) =
    if k >= 0
    then Ok(([k, k + 1], [k * 2]))
    else Error("negative")

let forward (k: Int) =
    match produce(k) with
        | Ok((first, second)) -> Ok((k, first, second))
        | Error(message) -> Error(message)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match forward(n) with
            | Ok((_k, first, second)) -> rounds(n - 1)(total + Ashes.Collection.List.length(first) + Ashes.Collection.List.length(second))
            | Error(_) -> rounds(n - 1)(total)

0
|> rounds(1000)
|> Ashes.IO.print
