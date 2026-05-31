// expect: 12
let calc = 
    fun (x) -> 
        let flags = (x | 2) ^ 1
        in 
            let shifted = (flags & 6) << 2
            in shifted >> 1
in Ashes.IO.print(calc(5))
