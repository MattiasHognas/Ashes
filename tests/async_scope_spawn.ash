// expect: 7
import Ashes.IO
let worker =
    async(match await Ashes.Task.fork(async 7) with
        | Error(error) -> Ashes.IO.print(error)
        | Ok(child) ->
            match await Ashes.Task.join(child) with
                | Error(error) -> Ashes.IO.print(error)
                | Ok(value) -> Ashes.IO.print(value))

let program =
    async(let _ = Ashes.Task.spawn(worker)
    in
        match await Ashes.Task.sleep(20) with
            | Error(error) -> Ashes.IO.print(error)
            | Ok(_) -> Ashes.IO.write(""))
in Ashes.Task.run(program)
