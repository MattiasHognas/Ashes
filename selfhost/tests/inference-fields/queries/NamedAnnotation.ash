// expect: named annotation query passed
type Rank = Rank(Int)
    deriving {Eq}

let recursive same left right = left == right

if same(((given (value: Rank) -> value))(Rank(7)))(Rank(7))
then Ashes.IO.print("named annotation query passed")
else Ashes.IO.panic("named annotation query failed")
