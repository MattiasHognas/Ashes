// Structured, deterministic parallelism over pure functions. The result of every
// operation here is IDENTICAL to its sequential equivalent — purity guarantees
// order-independence — so code written with these combinators is always correct.
//
// `both` (the single fork/join primitive) is a compiler intrinsic registered in
// BuiltinRegistry — it must be lowered at each call site so it can deep-copy a worker's
// result at the concrete result type — so it is NOT defined here. At concrete result types
// `both` runs its right thunk on a worker thread; polymorphic uses fall back to sequential
// evaluation (always correct). `map`/`reduce` below are ordinary Ashes and are sequential
// (they cannot route through the parallel `both` because their element type is abstract
// inside the polymorphic body).
//
// Self-contained: uses only core language features.

let rec plLength xs = 
    match xs with
        | [] -> 0
        | _h :: t -> 1 + plLength(t)

let rec plTake xs n = 
    if n <= 0
    then []
    else 
        match xs with
            | [] -> []
            | h :: t -> h :: plTake(t)(n - 1)

let rec plDrop xs n = 
    if n <= 0
    then xs
    else 
        match xs with
            | [] -> []
            | _h :: t -> plDrop(t)(n - 1)

let rec plAppend xs ys = 
    match xs with
        | [] -> ys
        | h :: t -> h :: plAppend(t)(ys)

let rec map f xs = 
    match xs with
        | [] -> []
        | h :: [] -> f(h) :: []
        | _ -> 
            let half = plLength(xs) / 2
            in 
                let leftRes = map(f)(plTake(xs)(half))
                in 
                    let rightRes = map(f)(plDrop(xs)(half))
                    in plAppend(leftRes)(rightRes)

let rec reduce combine identity f xs = 
    match xs with
        | [] -> identity
        | h :: [] -> f(h)
        | _ -> 
            let half = plLength(xs) / 2
            in 
                let leftRes = reduce(combine)(identity)(f)(plTake(xs)(half))
                in 
                    let rightRes = reduce(combine)(identity)(f)(plDrop(xs)(half))
                    in combine(leftRes)(rightRes)
