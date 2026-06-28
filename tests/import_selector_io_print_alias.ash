// expect: aliased
// fmt-skip: `ashes fmt` mangles selector imports (import M.binding as x -> import M as x)
import Ashes.IO.print as p
p("aliased")
