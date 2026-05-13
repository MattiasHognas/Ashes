// expect: a|bc
match Ashes.Text.uncons("abc") with
    | None -> Ashes.IO.print("empty")
    | Some((head, tail)) -> Ashes.IO.print(head + "|" + tail)
