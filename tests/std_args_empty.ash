// expect: []
match Ashes.IO.args with
    | [] -> Ashes.IO.print("[]")
    | _ -> Ashes.IO.print("bad")
