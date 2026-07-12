// expect: PASS

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
