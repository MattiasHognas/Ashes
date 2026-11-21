// expect: p21|q321
import Ashes.Task as task
let apply f n text = f(n)(text)

let job count =
    async(
        let recursive advance n text =
            if n == 0
            then text
            else
                match await task.sleep(1) with
                    | Ok(_tick) -> advance(n - 1)(text + Ashes.Text.fromInt(n))
                    | Error(message) -> message
        in
            let partial = advance(count)
            in
                let first = partial("p")
                in
                    let second = apply(advance)(count + 1)("q")
                    in first + "|" + second
    )

match 2
|> job
|> task.run with
    | Ok(text) -> Ashes.IO.print(text)
    | Error(message) -> Ashes.IO.print(message)
