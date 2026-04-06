// expect: 10
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Async.all([async 3, async 7])) with
    | Ok(results) ->
        match results with
            | a :: b :: [] -> a + b
            | _ -> 0
    | Error(_) -> 0)
