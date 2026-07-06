// Ashes.Net.Tcp.Server — TCP server primitives and the serve combinator.
//
// `listen`, `accept` and `forkWorkers` are compiler intrinsics (registered in BuiltinRegistry); the
// combinators below are library code built on top of them plus Ashes.Net.Tcp.send/receive/close.
// `serve` is parallel by default: it runs one independent reactor per online CPU (a fork-based
// multi-reactor), so an endpoint scales across cores without the program choosing a worker count.
// Each reactor binds the port, then loops accepting connections, spawning the handler on each
// (Ashes.Async.spawn), so a slow handler never blocks the accept loop. The handler owns its
// connection (it should close the socket when done) and runs detached — its result is dropped and its
// failure is isolated to its connection, never stopping the loop. A listener/bind failure ends the
// reactor (Error).
import Ashes.Net.Tcp
import Ashes.Async
let serveOne port handler = 
    async(let recursive loop listener = 
        match await Ashes.Net.Tcp.Server.accept(listener) with
            | Error(e) -> Error(e)
            | Ok(client) -> 
                let _ = Ashes.Async.spawn(handler(client))
                in loop(listener)
    in 
        match await Ashes.Net.Tcp.Server.listen(port) with
            | Error(e2) -> Error(e2)
            | Ok(listener) -> loop(listener))

let serveParallel port workers handler = 
    async(match await Ashes.Net.Tcp.Server.forkWorkers(port)(workers) with
        | Error(e) -> Error(e)
        | Ok(_idx) -> await serveOne(port)(handler))

let serve port handler = serveParallel(port)(0)(handler)
