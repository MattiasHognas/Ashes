// expect: 0
match Ashes.Process.spawn("/usr/bin/true")([]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(proc) -> Ashes.IO.print(Ashes.Process.waitForExit(proc))
