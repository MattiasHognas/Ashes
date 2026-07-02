// tls-server: accept
// skip-on: win-x64
// tls-expect: GET /x HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n
// tls-send: HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhi
// expect: AstartB1B2Agothi
// A second task makes progress while a network request is in flight. Under Ashes.Async.all, task A
// starts an HTTP GET and suspends on the socket (non-blocking connect/recv register an epoll wait);
// the scheduler then runs task B to completion (B1, B2) before the loopback response arrives, so B's
// output lands BETWEEN A's "Astart" and its "Agot"/body. A blocking request would instead run A to
// completion first (AstartAgothiB1B2). HTTPS variant: the TLS handshake also yields cooperatively (WaitTlsWantRead/Write).
// target.
import Ashes.IO
let taskA = 
    async(let _ = Ashes.IO.write("Astart")
    in 
        match await Ashes.Http.get("https://localhost:__TCP_PORT__/x") with
            | Ok(t) -> 
                let _ = Ashes.IO.write("Agot")
                in Ashes.IO.write(t)
            | Error(m) -> Ashes.IO.write(m))

let taskB = 
    async(let _ = Ashes.IO.write("B1")
    in 
        match await Ashes.Async.task(0) with
            | Ok(_) -> Ashes.IO.write("B2")
            | Error(_) -> Ashes.IO.write("Berr"))
in 
    match Ashes.Async.run(Ashes.Async.all([taskA, taskB])) with
        | Ok(_) -> Ashes.IO.write("")
        | Error(_) -> Ashes.IO.write("end-err")
