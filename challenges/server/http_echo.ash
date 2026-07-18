// HTTP benchmark target: a minimal HTTP/1.1 server on port 18081 (Ashes.Net.Http.Server.serve, parallel by default).
// Every request returns a fixed 200 "ok", so the benchmark measures the HTTP server path (request
// parse + response render + the TCP/scheduling underneath) rather than handler work. serve is
// parallel by default (one reactor per core), so this exercises multi-core scaling. challenges/ is CI-excluded
// and not format-checked. Driven by bench.sh.
import Ashes.IO
import Ashes.Net.Http.Server
import Ashes.Task
let onRequest _req = async(Ashes.Net.Http.Server.text(200)("ok"))
in
    match Ashes.Task.run(Ashes.Net.Http.Server.serve(18081)(onRequest)) with
        | Ok(_u) -> Ashes.IO.writeLine("server stopped")
        | Error(e) -> Ashes.IO.writeLine(e)
