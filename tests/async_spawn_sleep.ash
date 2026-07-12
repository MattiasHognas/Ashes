// expect: spawned main
import Ashes.IO
import Ashes.Async
let spawned =
    async(match await Ashes.Async.sleep(50) with
        | Ok(_x) -> Ashes.IO.write("spawned ")
        | Error(_e) -> Ashes.IO.write("spawned-err "))

let _s = Ashes.Async.spawn(spawned)
in
    match Ashes.Async.run(async(match await Ashes.Async.sleep(400) with
        | Ok(_y) -> Ashes.IO.writeLine("main")
        | Error(_e2) -> Ashes.IO.writeLine("main-err"))) with
        | Ok(_u) -> Ashes.IO.write("")
        | Error(e) -> Ashes.IO.print(e)
