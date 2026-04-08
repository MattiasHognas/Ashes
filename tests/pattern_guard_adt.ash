// expect: big ok
type Outcome =
    | Good(Int)
    | Bad(String)

let describe = 
    fun (r) -> 
        match r with
            | Good(n) when n >= 11 -> "big ok"
            | Good(n) -> "small ok"
            | Bad(e) -> e
in Ashes.IO.print(describe(Good(42)))
