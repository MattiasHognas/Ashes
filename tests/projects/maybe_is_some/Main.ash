// expect: true
import Ashes.Core.Maybe
Some(1)
|> Maybe.isSome
|> Ashes.IO.print
