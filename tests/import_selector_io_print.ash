// expect: hello
// fmt-skip: `ashes fmt` mangles selector imports (import M.binding -> import M as binding)
import Ashes.IO.print
print("hello")
