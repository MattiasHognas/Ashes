// Ashes.Net.Tls.Server — server-side TLS: the handshake intrinsic and the serveTls combinator.
//
// `handshake` is a compiler intrinsic (registered in BuiltinRegistry): it runs the server half of a
// TLS handshake over an accepted TCP socket, using the certificate-chain PEM and private-key PEM
// CONTENTS (not paths). The server config is built once and cached for the process. `serveTls` is
// the library combinator: it reads the PEM files up front (failing fast with a readable error),
// then serves like Ashes.Net.Tcp.Server.serve — concurrently, one spawned handler per connection —
// with a TLS handshake in front of each handler. The handler receives a TlsSocket and owns it
// (close it when done); a failed handshake closes the TCP socket and is isolated to that connection.
import Ashes.Net.Tcp
import Ashes.Net.Tcp.Server
import Ashes.IO.File
import Ashes.Task
let serveTls port certPath keyPath handler =
    match Ashes.IO.File.readText(certPath) with
        | Error(certErr) -> Ashes.Task.fromResult(Error("tls certificate: " + certErr))
        | Ok(certPem) ->
            match Ashes.IO.File.readText(keyPath) with
                | Error(keyErr) -> Ashes.Task.fromResult(Error("tls private key: " + keyErr))
                | Ok(keyPem) ->
                    Ashes.Net.Tcp.Server.serve(port)(given (client) ->
                        async(match await Ashes.Net.Tls.Server.handshake(client)(certPem)(keyPem) with
                            | Error(hsErr) -> Error(hsErr)
                            | Ok(tls) -> await handler(tls)))
