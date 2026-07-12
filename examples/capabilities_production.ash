capability Prices =
    | lookup : Str -> Int

capability Clock =
    | now : Unit -> Int

capability Log =
    | log : Str -> Unit

type Receipt =
    | item: Str
    | base: Int
    | tax: Int
    | total: Int
    | stamp: Int

let taxFor : Int -> Int =
    given (cents) -> cents / 10

let priceOf : Str -> Int needs {Prices} =
    given (item) -> perform Prices.lookup(item)

let processOrder =
    given (item) ->
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

let runProduction =
    given (work) ->
        handle work(Unit) with
            | Prices.lookup(item) -> resume(200)
            | Clock.now(_) -> resume(1000)
            | Log.log(msg) ->
                let _ = Ashes.IO.writeLine("[log] " + msg)
                in resume(Unit)
            | return(r) -> r

let receipt =
    runProduction(given (_) -> processOrder("widget"))

Ashes.IO.print(receipt.item + " total=" + Ashes.Text.fromInt(receipt.total) + " stamp=" + Ashes.Text.fromInt(receipt.stamp))
