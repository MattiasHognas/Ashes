// expect: UserId(42)

import Ashes.Text
type UserId = UserId(Int)
    deriving {Show}

Ashes.IO.print(Ashes.Trait.Show.show(UserId(42)))
