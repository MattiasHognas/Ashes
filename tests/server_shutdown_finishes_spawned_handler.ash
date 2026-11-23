// expect: handler drained
import Ashes.IO
import Ashes.Task
let handler =
    async(let _ = Stop.stop(Unit)
    in
        match await Ashes.Task.sleep(75) with
            | Ok(_) -> Ashes.IO.write("handler ")
            | Error(message) -> Ashes.IO.print(message))

let run =
    async(match await Ashes.Net.Tcp.Server.forkWorkers(0)(1) with
        | Error(message) -> Error(message)
        | Ok(_) ->
            match await Ashes.Net.Tcp.Server.listen(0) with
                | Error(message) -> Error(message)
                | Ok(listener) ->
                    let _ = Ashes.Net.Tcp.Server.setDrainTimeout(3000)
                    in
                        let _ = Ashes.Task.spawn(handler)
                        in
                            match await Ashes.Net.Tcp.Server.accept(listener) with
                                | Error("__ashes_server_shutdown") -> Ok(Unit)
                                | Error(message) -> Error(message)
                                | Ok(_) -> Error("unexpected connection"))

match Ashes.Task.run(run) with
    | Ok(_) -> Ashes.IO.print("drained")
    | Error(message) -> Ashes.IO.print(message)
