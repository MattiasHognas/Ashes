// expect: 142
// fmt-skip: `ashes fmt` mangles selector imports (import M.binding -> import M as binding)
import Ashes.IO.print
import MyMod.fn
print(fn(42))
