// expect: bounded
import Ashes.IO
import Ashes.IO.Console as console
import Ashes.Task
let handler =
    async(let _ = Stop.stop(Unit)
    in
        match await Ashes.Task.sleep(3000) with
            | Ok(_) -> Ashes.IO.print("handler passed drain deadline")
            | Error(message) -> Ashes.IO.print(message))

let run =
    async(match await Ashes.Net.Tcp.Server.forkWorkers(0)(1) with
        | Error(message) -> Error(message)
        | Ok(_) ->
            match await Ashes.Net.Tcp.Server.listen(0) with
                | Error(message) -> Error(message)
                | Ok(listener) ->
                    let _ = Ashes.Net.Tcp.Server.setDrainTimeout(75)
                    in
                        let started = console.monotonicMillis(Unit)
                        in
                            let _ = Ashes.Task.spawn(handler)
                            in
                                match await Ashes.Net.Tcp.Server.accept(listener) with
                                    | Error("__ashes_server_shutdown") ->
                                        if console.monotonicMillis(Unit) - started >= 75
                                        then Ok(Unit)
                                        else Error("drain returned before deadline")
                                    | Error(message) -> Error(message)
                                    | Ok(_) -> Error("unexpected connection"))

match Ashes.Task.run(run) with
    | Ok(_) -> Ashes.IO.print("bounded")
    | Error(message) -> Ashes.IO.print(message)
