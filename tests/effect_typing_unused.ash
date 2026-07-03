// expect: 42

effect Prices =
    | lookup : Str -> Int

effect Log =
    | log

effect State(a) =
    | get : Unit -> a
    | set : a -> Unit

let priceOf : Str -> Int uses {Prices} = 
    fun (item) -> perform Prices.lookup(item)

let logged = 
    fun (m) -> 
        let _ = perform Log.log(m)
        in Log.log(m)

let passthrough : Str -> Int uses {Prices | e} = 
    fun (item) -> priceOf(item)

Ashes.IO.print(42)
