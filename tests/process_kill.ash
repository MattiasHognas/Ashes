// expect: ok
// skip-on: win-x64
match Ashes.IO.Process.spawn("/usr/bin/sleep")(["100"]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(proc) ->
        let _ = Ashes.IO.Process.kill(proc)
        in
            let _ = Ashes.IO.Process.waitForExit(proc)
            in Ashes.IO.print("ok")
