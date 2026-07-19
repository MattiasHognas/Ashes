// expect-compile-error: ASH008
// Storing a FileHandle into an aggregate (Some(fh)) moves ownership into that aggregate; reading the
// original handle afterward is use-after-move (ASH008). Contrast resource_aggregate_escape.ash, where
// the aggregate escapes and the original is never touched again — that stays valid.
match Ashes.IO.File.open("input.txt") with
    | Error(_) -> Ashes.IO.print("error")
    | Ok(fh) ->
        let wrapped = Some(fh)
        in
            match Ashes.IO.File.readLine(fh) with
                | None -> Ashes.IO.print("none")
                | Some(line) -> Ashes.IO.print(line)
