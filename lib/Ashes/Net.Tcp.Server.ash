// Ashes.Net.Tcp.Server — TCP server primitives and the serve combinator.
//
// `listen` and `accept` are compiler intrinsics (registered in BuiltinRegistry); `serve` is the
// library combinator built on top of them plus Ashes.Net.Tcp.send/receive/close. `serve` binds the
// port, then loops accepting connections, spawning the handler on each (Ashes.Async.spawn), so
// connections are served concurrently: a slow handler never blocks the accept loop. The handler owns
// its connection (it should close the socket when done) and runs detached — its result is dropped
// and its failure is isolated to its connection, never stopping the loop. A listener/bind failure
// ends the server (Error).
import Ashes.Net.Tcp
import Ashes.Async
let serve port handler = 
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
