// expect: 2
let mk n = Ashes.Text.fromInt(n)

let recursive count n acc =
    if n == 0
    then 0
    else
        let s = mk(n)
        in
            if n % 2 == 0
            then 1 + count(n - 1)(s :: acc)
            else count(n - 1)(s :: acc)

Ashes.IO.print(count(4)([]))
