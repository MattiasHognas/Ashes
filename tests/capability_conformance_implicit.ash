// expect: widget total=220 stamp=1000

capability Prices =
    | lookup

capability Clock =
    | now

capability Log =
    | log

type Receipt =
    | item: Str
    | base: Int
    | tax: Int
    | total: Int
    | stamp: Int

let taxFor = 
    given (cents) -> cents / 10

let priceOf = 
    given (item) -> Prices.lookup(item)

let processOrder = 
    given (item) -> 
        let _ = Log.log("processing " + item)
        in 
            let base = Prices.lookup(item)
            in 
                let tax = taxFor(base)
                in 
                    let total = base + tax
                    in 
                        let _ = Log.log("total " + Ashes.Text.fromInt(total))
                        in 
                            let t = Clock.now(Unit)
                            in Receipt(item = item, base = base, tax = tax, total = total, stamp = t)

let runTest = 
    given (work) -> 
        handle work(Unit) with
            | Prices.lookup(item) -> resume(200)
            | Clock.now(_) -> resume(1000)
            | Log.log(msg) -> resume(Unit)
            | return(r) -> r

let receipt = 
    runTest(given (_) -> processOrder("widget"))

Ashes.IO.print(receipt.item + " total=" + Ashes.Text.fromInt(receipt.total) + " stamp=" + Ashes.Text.fromInt(receipt.stamp))
