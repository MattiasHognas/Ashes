// expect: 1 1001

capability Prices =
    | lookup : Str -> Int

let priceOf = 
    given (item) -> Prices.lookup(item)

let runCheap = 
    given (work) -> 
        handle work(Unit) with
            | Prices.lookup(_) -> resume(1)
            | return(r) -> r

let runRich = 
    given (work) -> 
        handle work(Unit) with
            | Prices.lookup(_) -> resume(1000)
            | return(r) -> r + 1

let a = 
    runCheap(given (_) -> priceOf("x"))

let b = 
    runRich(given (_) -> priceOf("x"))

Ashes.IO.print(Ashes.Text.fromInt(a) + " " + Ashes.Text.fromInt(b))
