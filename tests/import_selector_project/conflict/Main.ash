// expect-compile-error: Conflicting unqualified import selectors for 'x'.
import A.x
import B.x
Ashes.IO.print(x)
