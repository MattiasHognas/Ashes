// expect-compile-error: Refutable pattern in let binding
// fmt-skip: the program is rejected before it parses, so it has no canonical form
let xs = [1, 2, 3]
let first =
    let x :: rest = xs
    in x

Ashes.IO.print(first)
