// expect: 10
let task = 
    async
        match await Ashes.Async.all([async
            3, async
            7]) with
            | Error(_) -> 0
            | Ok(results) ->
                match results with
                    | a :: b :: [] -> a + b
                    | _ -> 0
in 
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
