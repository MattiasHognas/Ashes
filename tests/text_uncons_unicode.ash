// expect: é|!
match Ashes.Text.uncons("é!") with
    | None -> Ashes.IO.print("empty")
    | Some((head, tail)) -> Ashes.IO.print(head + "|" + tail)
