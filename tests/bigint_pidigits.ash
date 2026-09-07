// expect: 314159265358979323846264338327
// Unbounded spigot (Gibbons) streaming pi digits, driven entirely by Ashes.Number.BigInt.
// Verifies the first 30 digits of pi against the known value.
import Ashes.IO as io
import Ashes.Number.BigInt as big
let ten = big.fromInt(10)

let three = big.fromInt(3)

let four = big.fromInt(4)

let seven = big.fromInt(7)

let two = big.fromInt(2)

let one = big.fromInt(1)

let zero = big.fromInt(0)

let recursive g q r t k n l count acc =
    if count == 0
    then acc
    else
        if big.compare(big.sub(big.add(big.mul(four)(q))(r))(t))(big.mul(n)(t)) < 0
        then
            g(big.mul(ten)(q))(t
            |> big.mul(n)
            |> big.sub(r)
            |> big.mul(ten))(t)(k)(n
            |> big.mul(ten)
            |> big.sub(big.div(r
            |> big.add(big.mul(three)(q))
            |> big.mul(ten))(t)))(l)(count - 1)(acc + Ashes.Text.fromBigInt(n))
        else
            g(big.mul(q)(k))(big.mul(big.add(big.mul(two)(q))(r))(l))(big.mul(t)(l))(big.add(k)(one))(l
            |> big.mul(t)
            |> big.div(l
            |> big.mul(r)
            |> big.add(two
            |> big.add(big.mul(seven)(k))
            |> big.mul(q))))(big.add(l)(two))(count)(acc)

""
|> g(one)(zero)(one)(one)(three)(three)(30)
|> io.print
