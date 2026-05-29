// expect: 6
let task = async
    let pair = await Ashes.Async.all([async 1, async 2]) in
    let sum = match pair with
        | a :: b :: [] -> a + b
        | _ -> 0
    in let both = await Ashes.Async.all([Ashes.Async.fromResult(Ok(sum)), async 3]) in
    match both with
        | a :: b :: [] -> a + b
        | _ -> 0
in Ashes.IO.print(match Ashes.Async.run(task) with
    | Ok(n) -> n
    | Error(_) -> 0)
