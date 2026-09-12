// expect: 11
import Ashes.Core.Maybe
None
|> Maybe.unwrapOr(11)
|> Ashes.IO.print
