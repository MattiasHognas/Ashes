// expect-compile-error: ASH008
// Passing a FileHandle to a function that CONSUMES it (here, stores it in a returned aggregate) moves
// ownership into that function; reading the handle afterward in the caller is use-after-move (ASH008),
// distinct from use-after-close (ASH006). A pure-read helper would instead borrow (no move).
let stash =
    given (fh) -> Some(fh)
in
    match Ashes.IO.File.open("input.txt") with
        | Error(_) -> Ashes.IO.print("error")
        | Ok(fh) ->
            let _ = stash(fh)
            in
                match Ashes.IO.File.readLine(fh) with
                    | None -> Ashes.IO.print("none")
                    | Some(line) -> Ashes.IO.print(line)
