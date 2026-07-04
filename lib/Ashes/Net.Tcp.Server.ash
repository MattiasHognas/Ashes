// Ashes.Net.Tcp.Server — TCP server primitives and the serve combinator.
//
// `listen` and `accept` are compiler intrinsics (registered in BuiltinRegistry); `serve` is the
// library combinator built on top of them plus Ashes.Net.Tcp.send/receive/close. `serve` binds the
// port, then loops accepting connections and running the handler on each; the handler owns its
// connection (the socket auto-drops when the handler task completes). A listener/bind failure ends
// the server (Error); a handler's own failure is isolated to its connection and never stops the loop.
// This first version serves connections sequentially (one at a time); concurrent serving is future work.
import Ashes.Net.Tcp
let serve port handler = 
    async(let recursive loop listener = 
        match await Ashes.Net.Tcp.Server.accept(listener) with
            | Error(e) -> Error(e)
            | Ok(client) -> 
                let _ = await handler(client)
                in loop(listener)
    in 
        match await Ashes.Net.Tcp.Server.listen(port) with
            | Error(e2) -> Error(e2)
            | Ok(listener) -> loop(listener))
