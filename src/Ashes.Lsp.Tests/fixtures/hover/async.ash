let work =
    async(let a = await async 1
    in
        let b = await async 2
        in
            match (a, b) with
                | (Ok(x), Ok(y)) -> x + y
                | _ -> 0)

let checked =
    (let? v = Ok(3)
    in Ok(v + 1))

match Ashes.Task.run(work) with
    | Ok(n) -> Ashes.IO.print(n)
    | Error(_) -> Ashes.IO.print(0)
