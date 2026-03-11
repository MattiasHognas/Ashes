// expect: []
match Ashes.args with
    | [] -> Ashes.print("[]")
    | _ -> Ashes.print("bad")
