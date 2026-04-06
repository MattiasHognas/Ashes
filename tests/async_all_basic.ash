// expect: 10
let task =
    async
        let results =
            await Ashes.Async.all([async
                3, async
                7])
        in
            match results with
                | a :: b :: [] -> a + b
                | _ -> 0
in
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
