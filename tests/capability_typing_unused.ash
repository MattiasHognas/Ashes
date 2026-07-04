// expect: 42

capability Prices =
    | lookup : Str -> Int

capability Log =
    | log

capability State(a) =
    | get : Unit -> a
    | set : a -> Unit

let priceOf : Str -> Int needs {Prices} = 
    given (item) -> perform Prices.lookup(item)

let logged = 
    given (m) -> 
        let _ = perform Log.log(m)
        in Log.log(m)

let passthrough : Str -> Int needs {Prices | e} = 
    given (item) -> priceOf(item)

Ashes.IO.print(42)
