// expect: 5
import Ashes.Result
Ashes.IO.print(Result.default(5)(Error("nope")))
