// expect: 62
import Ashes.Task as task
let apply f a b = f(a)(b)

let job n =
    async(
        let recursive loop k total =
            if k <= 0
            then total
            else
                match await task.sleep(1) with
                    | Ok(_tick) -> loop(k - 1)(total + 1)
                    | Error(_message) -> total
        in
            let direct = loop(n)(10)
            in
                let partial = loop(0)
                in
                    let indirect = apply(loop)(0)(30)
                    in direct + partial(20) + indirect
    )

match 2
|> job
|> task.run with
    | Ok(total) -> Ashes.IO.print(total)
    | Error(message) -> Ashes.IO.print(message)
