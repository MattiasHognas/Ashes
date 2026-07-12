// expect: ok
let headOr xs def =
    match xs with
        | [] -> def
        | x :: _ -> x
in
    let _a = headOr([1, 2, 3])(0)
    in
        let _b = headOr(["a", "b"])("z")
        in Ashes.IO.print("ok")
