// expect: 1
match Some(1) with
    | None -> Ashes.IO.print(0)
    | Some(_) -> Ashes.IO.print(1)
