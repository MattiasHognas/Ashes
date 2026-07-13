// tls-server: accept
// skip-on: win-x64
// Structured-parallelism + TLS coexistence (roadmap CO-6). The genuine
// fork/join `both` per-thread arena (win-x64: TEB ArbitraryUserPointer, gs:0x28)
// must not collide with the platform's own thread-local storage: heavy parallel
// work runs both before and after a real TLS round-trip, and both results must
// stay correct. win-x64 is empirically verified under local Wine (parallel forks
// + a full loopback handshake coexist, memory-bounded); like the other loopback
// TLS fixtures it is skipped in the Wine smoke-test for CI portability and
// covered by the native Windows CI runner. See tls_send_receive.ash.
// tls-expect: ping
// tls-send: pong
// expect: 499999500000|pong|499999500000
import Ashes.Parallel
import Ashes.Text
import Ashes.IO
import Ashes.Async
import Ashes.Net.Tls
let recursive psum lo hi =
    if hi - lo <= 1
    then lo
    else
        let mid = lo + (hi - lo) / 2
        in
            match Ashes.Parallel.both(given (u) -> psum(lo)(mid))(given (u) -> psum(mid)(hi)) with
                | (a, b) -> a + b

let before = psum(0)(1000000)

let tlsResult =
    match Ashes.Async.run(async(match await Ashes.Net.Tls.connect("localhost")(__TCP_PORT__) with
        | Error(err) -> err
        | Ok(sock) ->
            match await Ashes.Net.Tls.send(sock)("ping") with
                | Error(err) -> err
                | Ok(sent) ->
                    match await Ashes.Net.Tls.receive(sock)(64) with
                        | Error(err) -> err
                        | Ok(text) ->
                            let _ = await Ashes.Net.Tls.close(sock)
                            in text)) with
        | Ok(text) -> text
        | Error(err) -> err

let after = psum(0)(1000000)
in Ashes.IO.print(Ashes.Text.fromInt(before) + "|" + tlsResult + "|" + Ashes.Text.fromInt(after))
