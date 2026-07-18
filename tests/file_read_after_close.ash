// expect-compile-error: ASH006
// A FileHandle bound by a match arm (Ok(fh)) and read after an explicit close
// must be flagged as use-after-close, exactly like a let-bound resource.
match Ashes.IO.File.open("input.txt") with
    | Error(_) -> Ashes.IO.print("error")
    | Ok(fh) ->
        let _ = Ashes.IO.File.close(fh)
        in
            match Ashes.IO.File.readLine(fh) with
                | None -> Ashes.IO.print("none")
                | Some(line) -> Ashes.IO.print(line)
