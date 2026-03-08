let xs = ["a", "b", "c"]
in 
    Ashes.IO.print(match xs with
        | [] -> "empty"
        | x :: _ -> x)
