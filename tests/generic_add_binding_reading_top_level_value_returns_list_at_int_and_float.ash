// expect: 12,4 4.5,1.5
let times = 3

let recursive repeatAdd total x n =
    if n <= 0
    then total
    else repeatAdd(total + x)(x)(n - 1)

let tripledWithOriginal x =
    (let tripled = repeatAdd(x)(x)(times - 1)
    in [tripled, x])

let ints =
    4
    |> tripledWithOriginal
    |> Ashes.Collection.List.map(given (n) -> Ashes.Text.fromInt(n))

let floats =
    1.5
    |> tripledWithOriginal
    |> Ashes.Collection.List.map(given (f) -> Ashes.Text.fromFloat(f))

Ashes.IO.print(Ashes.Text.join(",")(ints) + " " + Ashes.Text.join(",")(floats))
