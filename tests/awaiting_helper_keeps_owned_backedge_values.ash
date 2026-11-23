// expect: 2=1=seed
import Ashes.Task
type Entry =
    | text: Str
    | previous: List(Str)

let job start =
    async(
        let recursive loop n entry ignored =
            if n == 0
            then entry.text
            else
                match await Ashes.Task.sleep(1) with
                    | Error(e) -> e
                    | Ok(_) ->
                        let label = Ashes.Text.fromInt(3 - n) + "=" + entry.text
                        in
                            match [Entry(text = label, previous = [entry.text])] with
                                | next :: _ -> loop(n - 1)(next)(label)
                                | [] -> "missing"
        in loop(2)(Entry(text = start, previous = []))("unused")
    )

match "seed"
|> job
|> Ashes.Task.run with
    | Ok(value) -> Ashes.IO.print(value)
    | Error(message) -> Ashes.IO.print(message)
