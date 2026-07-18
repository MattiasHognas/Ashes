// file: greeting.txt = hello
// expect: hello
// A FileHandle bound by a match arm (Ok(fh)), read before it is closed, is a
// legitimate use and must still compile and run.
match Ashes.IO.File.open("greeting.txt") with
    | Error(_) -> Ashes.IO.print("error")
    | Ok(fh) ->
        match Ashes.IO.File.readLine(fh) with
            | None ->
                let _ = Ashes.IO.File.close(fh)
                in Ashes.IO.print("none")
            | Some(line) ->
                let _ = Ashes.IO.File.close(fh)
                in Ashes.IO.print(line)
