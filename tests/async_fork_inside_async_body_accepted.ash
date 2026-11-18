// `fork` directly in the body of an `async` is accepted, also inside a `match` arm of that body.
// expect: 7
let child = async(3)

let parent =
    async(
        match await Ashes.Task.fork(child) with
            | Error(_error) -> 0
            | Ok(joined) ->
                match await Ashes.Task.join(joined) with
                    | Ok(value) -> value + 4
                    | Error(_error) -> 0
    )

match Ashes.Task.run(parent) with
    | Ok(value) -> Ashes.IO.print(value)
    | Error(error) -> Ashes.IO.print(error)
