// expect: 1
let task = 
    async
        match await Ashes.Async.all([]) with
            | Error(_) -> 0
            | Ok(results) ->
                match results with
                    | [] -> 1
                    | _ -> 0
in 
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
