// Benchmark target: a minimal TCP echo server on port 18080 built with the serve combinator.
// One receive + one send + close per connection — the smallest handler, so the benchmark measures
// the server path (accept/receive/send/close + scheduling) rather than handler work.
// Not part of any test/example suite (challenges/ is CI-excluded). Driven by bench.py.
import Ashes.IO
import Ashes.Net.Tcp
import Ashes.Net.Tcp.Server
import Ashes.Async
let handle client = 
    async(match await Ashes.Net.Tcp.receive(client)(4096) with
        | Error(e) -> Error(e)
        | Ok(msg) -> 
            match await Ashes.Net.Tcp.send(client)(msg) with
                | Error(e2) -> Error(e2)
                | Ok(_n) -> await Ashes.Net.Tcp.close(client))
in 
    match Ashes.Async.run(Ashes.Net.Tcp.Server.serve(18080)(handle)) with
        | Ok(_u) -> Ashes.IO.writeLine("server stopped")
        | Error(e) -> Ashes.IO.writeLine(e)
