// expect-compile-error: ASH008
// Passing a FileHandle to a function moves ownership into that function; reading the handle
// afterward in the caller is use-after-move (ASH008), distinct from use-after-close (ASH006).
let ignore =
    given (fh) -> 0
in
    match Ashes.IO.File.open("input.txt") with
        | Error(_) -> Ashes.IO.print("error")
        | Ok(fh) ->
            let _ = ignore(fh)
            in
                match Ashes.IO.File.readLine(fh) with
                    | None -> Ashes.IO.print("none")
                    | Some(line) -> Ashes.IO.print(line)
