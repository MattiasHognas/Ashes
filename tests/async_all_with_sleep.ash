// expect: 30
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Async.all([
        async
            let _ = await Ashes.Async.sleep(5)
            in 10,
        async
            let _ = await Ashes.Async.sleep(5)
            in 20])) with
    | Ok(results) ->
        match results with
            | a :: b :: [] -> a + b
            | _ -> 0
    | Error(_) -> 0)
