// expect: 7
import Ashes.Core.Maybe
Some(7)
|> Maybe.default(0)
|> Ashes.IO.print
