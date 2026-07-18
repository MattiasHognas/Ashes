// expect: hello
// skip-on: win-x64
match Ashes.IO.Process.spawn("/usr/bin/echo")(["hello"]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(proc) ->
        match Ashes.IO.Process.readStdoutLine(proc) with
            | None -> Ashes.IO.print("no output")
            | Some(line) ->
                let _ = Ashes.IO.Process.waitForExit(proc)
                in Ashes.IO.print(line)
