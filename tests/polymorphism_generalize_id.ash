// expect: hello
let id x = x
in
    let n = id(42)
    in Ashes.IO.print(id("hello"))
