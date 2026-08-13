// expect: innerouter
import Ashes.IO
let nested =
    Ashes.Task.scope(async(match await Ashes.Task.fork(Ashes.Task.sleep(600000)) with
        | Error(_) -> "inner-error"
        | Ok(_) -> "inner"))

let program =
    async(match await nested with
        | Error(_) -> "outer-error"
        | Ok(value) -> value + "outer")
in
    Ashes.IO.print(match Ashes.Task.run(program) with
        | Error(_) -> "run-error"
        | Ok(value) -> value)
