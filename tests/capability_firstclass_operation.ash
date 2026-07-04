// expect: 42

capability Prices =
    | lookup : Str -> Int

let lookupFn = Prices.lookup

let both = 
    given (f) -> f("a") + f("b")

let result = 
    handle both(lookupFn) with
        | Prices.lookup(item) -> 
            match item with
                | "a" -> resume(40)
                | _ -> resume(2)
        | return(r) -> r

Ashes.IO.print(result)
