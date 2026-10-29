// expect: 1814
let produce (k: Int) =
    if k >= 0
    then Ok(([k, k + 1], [k * 2]))
    else Error("negative")

let recursive restart (remaining: Int) (acc: List(Result(Str, (List(Int), List(Int))))) (total: Int) =
    if remaining == 0
    then total + Ashes.Collection.List.length(acc)
    else
        match produce(remaining) with
            | Ok((a, b)) ->
                restart(remaining - 1)(
                    (if remaining % 2 == 0
                    then Ok((a, b))
                    else Error("odd")) :: []
                )(total + Ashes.Collection.List.length(a) + Ashes.Collection.List.length(b))
            | Error(_) -> restart(remaining - 1)(acc)(total)

let recursive restartMaybe (remaining: Int) (acc: List(Maybe((List(Int), List(Int))))) (total: Int) =
    if remaining == 0
    then total + Ashes.Collection.List.length(acc)
    else
        match Some(([remaining], [remaining, remaining])) with
            | Some((a, b)) -> restartMaybe(remaining - 1)([Some((b, a))])(total + Ashes.Collection.List.length(b))
            | None -> restartMaybe(remaining - 1)(acc)(total)

Ashes.IO.print(restart(300)([])(0) + restartMaybe(456)([])(0))
