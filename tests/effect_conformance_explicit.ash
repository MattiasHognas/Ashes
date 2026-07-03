// expect: widget total=220 stamp=1000

effect Prices =
    | lookup : Str -> Int

effect Clock =
    | now : Unit -> Int

effect Log =
    | log : Str -> Unit

type Receipt =
    | item: Str
    | base: Int
    | tax: Int
    | total: Int
    | stamp: Int

let taxFor : Int -> Int = 
    fun (cents) -> cents / 10

let priceOf : Str -> Int uses {Prices} = 
    fun (item) -> perform Prices.lookup(item)

let processOrder = 
    fun (item) -> 
        let _ = perform Log.log("processing " + item)
        in 
            let base = perform Prices.lookup(item)
            in 
                let tax = taxFor(base)
                in 
                    let total = base + tax
                    in 
                        let _ = perform Log.log("total " + Ashes.Text.fromInt(total))
                        in 
                            let t = perform Clock.now(Unit)
                            in Receipt(item = item, base = base, tax = tax, total = total, stamp = t)

let runTest = 
    fun (work) -> 
        handle work(Unit) with
            | Prices.lookup(item) -> resume(200)
            | Clock.now(_) -> resume(1000)
            | Log.log(msg) -> resume(Unit)
            | return(r) -> r

let receipt = 
    runTest(fun (_) -> processOrder("widget"))

Ashes.IO.print(receipt.item + " total=" + Ashes.Text.fromInt(receipt.total) + " stamp=" + Ashes.Text.fromInt(receipt.stamp))
