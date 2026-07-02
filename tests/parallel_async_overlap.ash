// tcp-server: accept
// tcp-expect: GET /x HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n
// tcp-send: HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhi
// expect: 2|499999500000
// A `both` worker makes progress while an async I/O is live. Under Ashes.Async.all the scheduler
// steps taskA first: it starts an HTTP GET and suspends with the socket connect in flight. It then
// steps taskB, which forks a genuinely-parallel `both` (two sumRange halves, one on a worker thread)
// entirely within one coroutine segment. The worker runs concurrently with taskA's pending request,
// and both results are correct: taskA returns the response length (2 = "hi"), taskB the full sum.
// This is the concurrent overlap the cooperative scheduler enables — before it, the async runtime
// blocked, so a fork could only run before or after an I/O, never during one.
import Ashes.Parallel
import Ashes.Text
import Ashes.IO
import Ashes.Async
let rec sumRange lo hi acc = 
    if lo >= hi
    then acc
    else sumRange(lo + 1)(hi)(acc + lo)

let rec tlen text acc = 
    match Ashes.Text.uncons(text) with
        | None -> acc
        | Some((_h, t)) -> tlen(t)(acc + 1)

let taskA = 
    async(match await Ashes.Http.get("http://127.0.0.1:__TCP_PORT__/x") with
        | Ok(t) -> tlen(t)(0)
        | Error(_) -> -1)

let taskB = 
    async(match await Ashes.Async.task(0) with
        | Error(_) -> 0
        | Ok(_) -> 
            match Ashes.Parallel.both(fun (u) -> sumRange(0)(500000)(0))(fun (u) -> sumRange(500000)(1000000)(0)) with
                | (a, b) -> a + b)
in 
    match Ashes.Async.run(Ashes.Async.all([taskA, taskB])) with
        | Ok(results) -> 
            match results with
                | ra :: rb :: [] -> Ashes.IO.print(Ashes.Text.fromInt(ra) + "|" + Ashes.Text.fromInt(rb))
                | _ -> Ashes.IO.print("bad")
        | Error(_) -> Ashes.IO.print("err")
