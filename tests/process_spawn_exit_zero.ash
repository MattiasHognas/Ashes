// expect: 0
// skip-on: win-x64
match Ashes.IO.Process.spawn("/usr/bin/true")([]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(proc) ->
        proc
        |> Ashes.IO.Process.waitForExit
        |> Ashes.IO.print
