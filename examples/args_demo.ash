match Ashes.IO.args with
    | first :: second :: [] -> Ashes.IO.print(first + ":" + second)
    | first :: [] -> Ashes.IO.print(first)
    | _ -> Ashes.IO.print("no-args")
