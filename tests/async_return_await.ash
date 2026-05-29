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
            | Ok(n) -> n
            | Error(_) -> 0)
