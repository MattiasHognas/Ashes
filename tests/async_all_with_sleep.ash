// expect: 30
let task =
    async(match await Ashes.Async.all([async(let _ = await Ashes.Async.sleep(5)
    in 10), async(let _ = await Ashes.Async.sleep(5)
    in 20)]) with
        | Error(_) -> 0
        | Ok(results) ->
            match results with
                | a :: b :: [] -> a + b
                | _ -> 0)
in
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
