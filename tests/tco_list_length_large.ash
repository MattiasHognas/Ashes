// expect: 100000
let rec len xs acc = 
    match xs with
        | [] -> acc
        | _ :: rest -> len(rest)(acc + 1)
in 
    let rec build n acc = 
        if n <= 0
        then acc
        else build(n - 1)(1 :: acc)
    in Ashes.IO.print(len(build(100000)([]))(0))
