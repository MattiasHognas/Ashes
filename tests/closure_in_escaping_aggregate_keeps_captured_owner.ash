// expect: lit2q,lit2q,lit2q,lit2q,lit2q,a2q3qb2q|lit1q,lit1q,lit1q,lit1q,lit1q,a1q2qb1q|
// Each closure captures a reference-counted `let` owner and leaves its function inside an
// aggregate: a tuple, an Option, a record field, a list element, a tuple inside an Option, and two
// closures in a tuple sharing one owner. The owner is released before the aggregate is read.
type Box =
    | run: Str -> Str
    | k: Int

let tag (n: Int) = Ashes.Text.fromInt(n) + "q"

let inTuple (n: Int) =
    (let s = tag(n)
    in
        (given (x) -> x + s, n))

let inOption (n: Int) =
    (let s = tag(n)
    in
        Some(given (x) -> x + s))

let inRecord (n: Int) =
    (let s = tag(n)
    in
        Box(run = given (x) -> x + s, k = n))

let inList (n: Int) =
    (let s = tag(n)
    in
        [given (x) -> x + s])

let inNested (n: Int) =
    (let s = tag(n)
    in
        Some((given (x) -> x + s, n)))

let shared (n: Int) =
    (let s = tag(n)
    in
        let t = tag(n + 1)
        in
            (given (x) -> x + s, given (y) -> t + y + s))

let readTuple (n: Int) =
    match inTuple(n) with
        | (f, m) -> f("lit")

let readOption (n: Int) =
    match inOption(n) with
        | Some(f) -> f("lit")
        | None -> ""

let readRecord (n: Int) =
    (let r = inRecord(n)
    in r.run("lit"))

let readList (n: Int) =
    match inList(n) with
        | f :: _ -> f("lit")
        | [] -> ""

let readNested (n: Int) =
    match inNested(n) with
        | Some((f, m)) -> f("lit")
        | None -> ""

let readShared (n: Int) =
    match shared(n) with
        | (f, g) -> f("a") + g("b")

let probe (n: Int) = readTuple(n) + "," + readOption(n) + "," + readRecord(n) + "," + readList(n) + "," + readNested(n) + "," + readShared(n) + "|"

let recursive loop (n: Int) (acc: Str) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

""
|> loop(2)
|> Ashes.IO.print
