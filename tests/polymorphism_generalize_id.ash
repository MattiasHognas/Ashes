// expect: hello
let id = 
    fun (x) -> x
in 
    let n = id(42)
    in Ashes.IO.print(id("hello"))
