// expect: 43
import Ashes.Task as task
let job count =
    async(
        let recursive helper : Int -> Int =
            given (n) ->
                if n <= 0
                then 10
                else
                    match await task.sleep(1) with
                        | Ok(_tick) -> helper(n - 1)
                        | Error(_message) -> 0
        in
            let saved = helper
            in
                let capture n = saved(n)
                in
                    let recursive helper : Int -> Task(Str, Int) =
                        given (n) ->
                            async(match await task.sleep(1) with
                                | Ok(_tick) -> n + 20
                                | Error(_message) -> 0)
                    in
                        match await helper(2) with
                            | Ok(taskValue) ->
                                let helper n = n + 10
                                in capture(count) + taskValue + helper(1)
                            | Error(_message) -> 0
    )

match 1
|> job
|> task.run with
    | Ok(n) -> Ashes.IO.print(n)
    | Error(message) -> Ashes.IO.print(message)
