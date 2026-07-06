// HTTP benchmark target: a minimal HTTP/1.1 server on port 18081 (Ashes.Http.Server.serve). Every
// request returns a fixed 200 "ok", so the benchmark measures the HTTP server path (request parse +
// response render + the TCP/scheduling underneath) rather than handler work. challenges/ is
// CI-excluded and not format-checked. Driven by bench.sh.
import Ashes.IO
import Ashes.Http.Server
import Ashes.Async
let onRequest _req = async(Ashes.Http.Server.text(200)("ok"))
in 
    match Ashes.Async.run(Ashes.Http.Server.serve(18081)(onRequest)) with
        | Ok(_u) -> Ashes.IO.writeLine("server stopped")
        | Error(e) -> Ashes.IO.writeLine(e)
