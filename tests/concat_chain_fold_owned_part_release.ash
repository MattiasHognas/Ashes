// expect: 9617
import Ashes.Text
let recursive grow (n: Int) (acc: Str) =
    if n == 0
    then acc
    else grow(n - 1)(acc + "0123456789abcdef")

let bigText n = grow(n)("")

let describe (name: Str) =
    (let expected = bigText(300)
    in
        let actual = bigText(299)
        in
            if actual == expected
            then "same"
            else "mismatch for " + name + "\nexpected:\n" + expected + "actual:\n" + actual)

Ashes.IO.print(Ashes.Text.fromInt(Ashes.Text.length(describe("x"))))
