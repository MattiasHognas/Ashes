// expect: 1 1001

effect Prices =
    | lookup : Str -> Int

let priceOf = 
    fun (item) -> Prices.lookup(item)

let runCheap = 
    fun (work) -> 
        handle work(Unit) with
            | Prices.lookup(_) -> resume(1)
            | return(r) -> r

let runRich = 
    fun (work) -> 
        handle work(Unit) with
            | Prices.lookup(_) -> resume(1000)
            | return(r) -> r + 1

let a = 
    runCheap(fun (_) -> priceOf("x"))

let b = 
    runRich(fun (_) -> priceOf("x"))

Ashes.IO.print(Ashes.Text.fromInt(a) + " " + Ashes.Text.fromInt(b))
