// expect: 2
let rec len xs = 
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + len(rest)
in Ashes.IO.print(len(1 :: 2 :: []))
