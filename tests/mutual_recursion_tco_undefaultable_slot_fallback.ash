// expect: 2
type Shape =
    | Circle(Int)
    | Square(Int)

let recursive measure =
    given (n: Int) ->
        given (s: Shape) ->
            if n <= 0
            then
                match s with
                    | Circle(r) -> r
                    | Square(w) -> w
            else count(n - 1)(n)
and count =
    given (n: Int) ->
        given (k: Int) ->
            if n <= 0
            then k
            else measure(n - 1)(Circle(k))

Ashes.IO.print(measure(4)(Square(9)))
