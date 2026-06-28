// expect-compile-error: Conflicting unqualified import selectors for 'x'.
// fmt-skip: `ashes fmt` mangles selector imports (import M.binding -> import M as binding)
import A.x
import B.x
Ashes.IO.print(x)
