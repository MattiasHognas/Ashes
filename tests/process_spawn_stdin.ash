// expect: hello
match Ashes.Process.spawn("/bin/sh")(["-c", "read x; echo $x"]) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(proc) -> 
        let _ = Ashes.Process.writeStdin(proc)("hello\n")
        in 
            match Ashes.Process.readStdoutLine(proc) with
                | None -> Ashes.IO.print("no output")
                | Some(line) -> 
                    let _ = Ashes.Process.waitForExit(proc)
                    in Ashes.IO.print(line)
