// expect: ok
type Pair =
    | Pair(A, B)

let _p = Pair(1)(2)
in Ashes.IO.print("ok")
