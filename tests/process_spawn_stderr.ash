// expect: err
// skip-on: win-x64
match Ashes.IO.Process.spawn("/bin/sh")(["-c", "echo err >&2"]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(proc) ->
        match Ashes.IO.Process.readStderrLine(proc) with
            | None -> Ashes.IO.print("no output")
            | Some(line) ->
                let _ = Ashes.IO.Process.waitForExit(proc)
                in Ashes.IO.print(line)
