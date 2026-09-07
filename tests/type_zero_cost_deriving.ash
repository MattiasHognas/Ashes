// expect: UserId(42)

import Ashes.Text
type UserId = UserId(Int)
    deriving {Show}

UserId(42)
|> Ashes.Trait.Show.show
|> Ashes.IO.print
