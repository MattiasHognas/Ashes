// expect: 12 3.5 10 2.5
let describe n =
    (let double x = x + x
    in
        Ashes.Text.fromInt(double(n)) + " " + Ashes.Text.fromFloat(double(1.75)))

let both =
    (let double x = x + x
    in
        Ashes.Text.fromInt(double(5)) + " " + Ashes.Text.fromFloat(double(1.25)))

Ashes.IO.print(describe(6) + " " + both)
