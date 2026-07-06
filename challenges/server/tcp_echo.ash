// TCP benchmark target: a minimal TCP echo server on port 18080 (Ashes.Net.Tcp.Server.serve).
// One receive + echo + close per connection — the smallest handler, so the benchmark measures the
// server path (accept/receive/send/close + scheduling) rather than handler work. challenges/ is
// CI-excluded and not format-checked by any gate. Driven by bench.sh.
import Ashes.IO
import Ashes.Net.Tcp
import Ashes.Net.Tcp.Server
import Ashes.Async
let handleConn client = 
    async(match await Ashes.Net.Tcp.receive(client)(4096) with
        | Error(e) -> Error(e)
        | Ok(msg) -> 
            match await Ashes.Net.Tcp.send(client)(msg) with
                | Error(e2) -> Error(e2)
                | Ok(_n) -> await Ashes.Net.Tcp.close(client))
in 
    match Ashes.Async.run(Ashes.Net.Tcp.Server.serve(18080)(handleConn)) with
        | Ok(_u) -> Ashes.IO.writeLine("server stopped")
        | Error(e) -> Ashes.IO.writeLine(e)
