// expect: 6
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Async.all([async 1, async 2, async 3])) with
    | Ok(results) ->
        match results with
            | a :: b :: c :: [] -> a + b + c
            | _ -> 0
    | Error(_) -> 0)
