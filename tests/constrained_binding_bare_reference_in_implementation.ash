// expect: same
type Box(a) =
    | Box(a)

implement Eq(Box(a)) requires {Eq(a)} =
    | equal =
        let recursive go : Box(a) -> Box(a) -> Bool requires {Eq(a)} =
            given (left) ->
                given (right) ->
                    match (left, right) with
                        | (Box(x), Box(y)) -> x == y
        in go

let same (l: Box(Int)) (r: Box(Int)) = l == r

Ashes.IO.print(if same(Box(1))(Box(1))
then "same"
else "different")
