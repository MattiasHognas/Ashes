// expect: 778
import Caps
let result = 
    Caps.frozen(given (_) -> Clock.now(Unit) + 1)

Ashes.IO.print(result)
