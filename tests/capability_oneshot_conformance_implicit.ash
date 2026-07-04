// expect: PASS

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
            | Prices.lookup(item) -> 
                match item with
                    | "widget" -> resume(200)
                    | _ -> resume(0)
            | Clock.now(_) -> resume(1000)
            | Log.log(msg) -> 
                match resume(Unit) with
                    | (r, rest) -> (r, msg :: rest)
            | return(r) -> (r, [])

let result = 
    match runTest(given (_) -> processOrder("widget")) with
        | (r, logs) -> 
            match (r.base == 200, r.tax == 20, r.total == 220, r.stamp == 1000, logs) with
                | (true, true, true, true, "processing widget" :: "total 220" :: []) -> "PASS"
                | _ -> "FAIL"

Ashes.IO.print(result)
