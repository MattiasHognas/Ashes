let assertEqual = 
    fun (expected) -> 
        fun (actual) -> 
            if expected == actual
            then Unit
            else Ashes.panic("Assertion failed")
in 
    let fail = 
        fun (msg) -> Ashes.panic(msg)
    in fail
