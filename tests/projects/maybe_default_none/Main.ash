// expect: 0
import Ashes.Core.Maybe
None
|> Maybe.default(0)
|> Ashes.IO.print
