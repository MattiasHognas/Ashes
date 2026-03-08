// expect: empty
match Ashes.IO.args with
    | x :: _ -> Ashes.IO.print(x + "!")
    | [] -> Ashes.IO.print("empty")
