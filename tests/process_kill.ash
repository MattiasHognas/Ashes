// expect: ok
// skip-on: win-x64
match Ashes.Process.spawn("/usr/bin/sleep")(["100"]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(proc) -> 
        let _ = Ashes.Process.kill(proc)
        in 
            let _ = Ashes.Process.waitForExit(proc)
            in Ashes.IO.print("ok")
