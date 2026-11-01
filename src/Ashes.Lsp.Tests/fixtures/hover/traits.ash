type Shape =
    | Circle(Int)
    | Square(Int)
    deriving {Eq, Show}

trait Area(a) =
    | area : a -> Int
    | doubled : a -> Int =
        given (s) -> Area.area(s) * 2

implement Area(Shape) =
    | area =
        given (s) ->
            match s with
                | Circle(r) -> r * r * 3
                | Square(w) -> w * w

let recursive total : List(a) -> Int requires {Area(a)} =
    given (shapes) ->
        match shapes with
            | [] -> 0
            | s :: rest -> Area.area(s) + total(rest)

let same = Circle(1) == Circle(1)

let shown = Show.show(Square(2))

Ashes.IO.print(Area.doubled(Circle(2)) + Area.area(Square(3)))
