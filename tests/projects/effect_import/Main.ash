// expect: 778
import Caps
let result = 
    Caps.frozen(fun (_) -> Clock.now(Unit) + 1)

Ashes.IO.print(result)
