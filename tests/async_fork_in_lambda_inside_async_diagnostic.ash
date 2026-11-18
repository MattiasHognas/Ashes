// A lambda's body runs as a function of its own, so a `fork` inside a lambda that an `async` body
// builds is outside that body and is rejected.
// expect-compile-error: Ashes.Task.fork() must be used inside an async task.
let parent =
    async(
        let start =
            given (task) -> Ashes.Task.fork(task)
        in 2
    )

match Ashes.Task.run(parent) with
    | Ok(value) -> Ashes.IO.print(value)
    | Error(error) -> Ashes.IO.print(error)
