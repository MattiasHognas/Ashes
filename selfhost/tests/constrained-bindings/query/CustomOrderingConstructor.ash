// expect: custom constructor ordering passed
type Rank =
    | Rank(Int)
    deriving {Eq}

implement Ord(Rank) =
    | compare =
        given (left) ->
            given (right) ->
                match (left, right) with
                    | (Rank(x), Rank(y)) ->
                        if x < y
                        then Less
                        else
                            if x == y
                            then Equal
                            else Greater

let before left right = left <= right

if before(Rank(1))(Rank(2)) && before(Rank(2))(Rank(1)) == false
then Ashes.IO.print("custom constructor ordering passed")
else Ashes.IO.panic("constructor arguments lost their ordering evidence")
