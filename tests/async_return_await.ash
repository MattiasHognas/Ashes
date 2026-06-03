// expect: 42
let value = 
    async
        42
in 
    let wrapper = 
        async
            await value
    in 
        Ashes.IO.print(match Ashes.Async.run(wrapper) with
            | Ok(Ok(n)) -> n
            | Ok(Error(_)) -> 0
            | Error(_) -> 0)
