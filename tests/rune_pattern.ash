// expect: emoji
match '😀' with
    | 'A' -> Ashes.IO.print("latin")
    | '😀' -> Ashes.IO.print("emoji")
    | _ -> Ashes.IO.print("other")
