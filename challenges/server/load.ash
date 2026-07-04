// Load client for the echo server (dogfooding: client and server both in Ashes). Does `count`
// sequential connect/send/recv/close round-trips to 127.0.0.1:18080, where `count` is the first
// argument. Prints "ok" on success or an error marker. bench.sh runs several of these in parallel
// (one per concurrency worker) and times the batch with the shell clock to derive throughput.
import Ashes.IO
import Ashes.Net.Tcp
import Ashes.Async
import Ashes.Text
let recursive loop n = 
    if n <= 0
    then 0
    else 
        match Ashes.Async.run(async(match await Ashes.Net.Tcp.connect("127.0.0.1")(18080) with
            | Error(_e) -> Error("connect")
            | Ok(sock) -> 
                match await Ashes.Net.Tcp.send(sock)("ping") with
                    | Error(_e2) -> Error("send")
                    | Ok(_s) -> 
                        match await Ashes.Net.Tcp.receive(sock)(4096) with
                            | Error(_e3) -> Error("recv")
                            | Ok(_msg) -> await Ashes.Net.Tcp.close(sock))) with
            | Ok(_u) -> loop(n - 1)
            | Error(_e) -> -n
in 
    match Ashes.IO.args with
        | count :: _ -> 
            match Ashes.Text.parseInt(count) with
                | Error(_e) -> Ashes.IO.writeLine("bad-count")
                | Ok(n) -> 
                    match loop(n) with
                        | 0 -> Ashes.IO.writeLine("ok")
                        | _r -> Ashes.IO.writeLine("err")
        | [] -> Ashes.IO.writeLine("no-count")
