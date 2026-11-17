// expect: constructor query passed
type Rank = Rank(Int)
    deriving {Eq}

let recursive same left right = left == right

if same(Rank(7))(Rank(7))
then Ashes.IO.print("constructor query passed")
else Ashes.IO.panic("constructor query failed")
