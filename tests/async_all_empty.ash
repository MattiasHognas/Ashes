// expect: 1
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Async.all([])) with
    | Ok(results) ->
        match results with
            | [] -> 1
            | _ -> 0
    | Error(_) -> 0)
