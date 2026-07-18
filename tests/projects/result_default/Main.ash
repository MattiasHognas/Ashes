// expect: 5
import Ashes.Core.Result
Ashes.IO.print(Result.default(5)(Error("nope")))
