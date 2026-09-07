// expect: 12
let calc x =
    let flags = (x | 2) ^ 1
    in
        let shifted = (flags & 6) << 2
        in shifted >> 1
in
    5
    |> calc
    |> Ashes.IO.print
