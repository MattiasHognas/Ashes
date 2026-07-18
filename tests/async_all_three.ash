// expect: 6
let task =
    async(match await Ashes.Task.all([async 1, async 2, async 3]) with
        | Error(_) -> 0
        | Ok(results) ->
            match results with
                | a :: b :: c :: [] -> a + b + c
                | _ -> 0)
in
    Ashes.IO.print(match Ashes.Task.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
