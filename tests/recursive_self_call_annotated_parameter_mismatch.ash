// expect-compile-error: Type mismatch: Int vs List<Int>. Context: in argument #2 of call to 'f'.
let recursive f (xs: List(Int)) (n: Int) =
    match xs with
        | [] -> n
        | _ :: rest -> f(rest)(rest)

Ashes.IO.print(f([1])(0))
