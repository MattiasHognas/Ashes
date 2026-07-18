// expect: 1
// skip-on: win-x64
match Ashes.IO.Process.spawn("/usr/bin/false")([]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(proc) -> Ashes.IO.print(Ashes.IO.Process.waitForExit(proc))
