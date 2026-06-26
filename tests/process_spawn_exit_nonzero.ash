// expect: 1
// skip-on: win-x64
match Ashes.Process.spawn("/usr/bin/false")([]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(proc) -> Ashes.IO.print(Ashes.Process.waitForExit(proc))
