// expect: hello
// skip-on: win-x64
match Ashes.IO.Process.spawn("/bin/sh")(["-c", "read x; echo $x"]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(proc) ->
        let _ = Ashes.IO.Process.writeStdin(proc)("hello\n")
        in
            match Ashes.IO.Process.readStdoutLine(proc) with
                | None -> Ashes.IO.print("no output")
                | Some(line) ->
                    let _ = Ashes.IO.Process.waitForExit(proc)
                    in Ashes.IO.print(line)
