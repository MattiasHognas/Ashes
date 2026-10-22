// A tuple of a generic value and a contract list comes back through closures: a generic function
// applying its closure parameter, the same twice over, a closure parameter of a concrete tuple
// type, a function that does not hand its parts over, and a lambda. Each caller asks whichever
// function it reaches for the tuple's contract parts, and releases the ones the callee says it
// handed over.
// expect: 180
type Item =
    | name: Str
    | n: Int

type Box =
    | items: List(Item)
    | count: Int

let make (k: Int) = [Item(name = "a" + Ashes.Text.fromInt(k), n = k)]

let makeBox (k: Int) = Box(items = make(k + 1), count = k)

let pick value (k: Int) =
    (let built = makeBox(k)
    in
        if k > 2
        then (value, make(k))
        else (value, built.items))

let concrete (k: Int) = (k, make(k))

let apply f (k: Int) = f(k)(k)

let applyTwice f (k: Int) = apply(f)(k)

let run (f: Int -> (Int, List(Item))) (k: Int) =
    match f(k) with
        | (m, items) -> m + Ashes.Collection.List.length(items)

let runItem (f: Int -> (Item, List(Item))) (k: Int) =
    match f(k) with
        | (item, items) -> item.n + Ashes.Collection.List.length(items)

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        let k = remaining - remaining / 5 * 5
        in
            let a =
                match apply(pick)(k) with
                    | (m, items) -> m + Ashes.Collection.List.length(items)
            in
                let b =
                    match applyTwice(pick)(k) with
                        | (m, items) -> m + Ashes.Collection.List.length(items)
                in
                    let c =
                        run(pick(k))(k)
                    in
                        let d = run(concrete)(k)
                        in
                            let e =
                                runItem(pick(Item(name = "i" + Ashes.Text.fromInt(k), n = k)))(k)
                            in
                                let g =
                                    match apply(given (x: Int) ->
                                        given (y: Int) -> (x, make(y)))(k) with
                                        | (m, items) -> m + Ashes.Collection.List.length(items)
                                in loop(remaining - 1)(total + a + b + c + d + e + g)

0
|> loop(10)
|> Ashes.IO.print
