// expect: empty
match Ashes.Text.uncons("") with
    | None -> Ashes.IO.print("empty")
    | Some((head, tail)) -> Ashes.IO.print(head + "|" + tail)
