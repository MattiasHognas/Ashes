// expect: 2
let recursive a n =
    match n with
        | 0 -> 0
        | _ -> b(n - 1)
and b n =
    match n with
        | 0 -> 1
        | _ -> c(n - 1)
and c n =
    match n with
        | 0 -> 2
        | _ -> a(n - 1)

Ashes.IO.print(a(2000000))
