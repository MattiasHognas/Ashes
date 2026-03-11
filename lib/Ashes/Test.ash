let assertEqual = 
    fun (expected) -> 
        fun (actual) -> 
            if expected == actual
            then Unit
            else Ashes.IO.panic("Assertion failed")
in 
    let fail = 
        fun (msg) -> Ashes.IO.panic(msg)
    in fail
