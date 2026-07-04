// expect: 120
// fmt-skip: exercises the bare `let f = let..in` RHS parse path; `ashes fmt` canonicalizes the RHS into a parenthesized `let f = (let..in)`, which would no longer cover this unit's fix.
let factorial = 
    (let recursive go = 
        given (n) -> 
            given (acc) -> 
                if n <= 0
                then acc
                else go(n - 1)(acc * n)
    in go)

let start = 1

Ashes.IO.print(factorial(5)(start))
