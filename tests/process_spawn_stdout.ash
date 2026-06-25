// expect: hello
// skip-on: win-x64
match Ashes.Process.spawn("/usr/bin/echo")(["hello"]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(proc) -> 
        match Ashes.Process.readStdoutLine(proc) with
            | None -> Ashes.IO.print("no output")
            | Some(line) -> 
                let _ = Ashes.Process.waitForExit(proc)
                in Ashes.IO.print(line)
