// expect: A1B1B2A2done
// Cooperative sleep scheduler: two tasks run concurrently on one thread and interleave across their
// sleeps. taskA writes A1, sleeps 50ms, writes A2; taskB writes B1, sleeps 10ms, writes B2. Under
// Ashes.Async.all the scheduler steps both to their first suspend (A1, then B1), then waits only until
// the EARLIEST deadline: B's 10ms fires first so B resumes and writes B2 while A is still sleeping,
// and only later does A's 50ms fire so it writes A2. The interleaved order A1 B1 B2 A2 is impossible
// with a blocking sleep (which would produce A1 A2 B1 B2, running the tasks to completion in turn).
// Writes (no newline) keep the observable ordering on a single line.
import Ashes.IO
let taskA = 
    async(let _ = Ashes.IO.write("A1")
    in 
        match await Ashes.Async.sleep(50) with
            | Error(_) -> 0
            | Ok(_) -> 
                let _ = Ashes.IO.write("A2")
                in 1)

let taskB = 
    async(let _ = Ashes.IO.write("B1")
    in 
        match await Ashes.Async.sleep(10) with
            | Error(_) -> 0
            | Ok(_) -> 
                let _ = Ashes.IO.write("B2")
                in 1)
in 
    match Ashes.Async.run(Ashes.Async.all([taskA, taskB])) with
        | Ok(_) -> Ashes.IO.write("done")
        | Error(_) -> Ashes.IO.write("err")
