// expect: err
match Ashes.Process.spawn("/bin/sh")(["-c", "echo err >&2"]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(proc) -> 
        match Ashes.Process.readStderrLine(proc) with
            | None -> Ashes.IO.print("no output")
            | Some(line) -> 
                let _ = Ashes.Process.waitForExit(proc)
                in Ashes.IO.print(line)
