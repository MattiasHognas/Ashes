// Structured, deterministic parallelism over pure functions (see
// docs/future/STRUCTURED_PARALLELISM.md). The result of every operation here is
// IDENTICAL to its sequential equivalent — purity guarantees order-independence —
// so code written with these combinators is always correct.
//
// EXECUTION IS CURRENTLY SEQUENTIAL. `both` is the single fork/join primitive;
// today it simply evaluates both thunks in order. The threading runtime (per-thread
// arenas + clone/futex + deep-copy-on-join — the deep-copy foundation already exists
// as Ashes.Internal.deepCopy) will replace `both` with a parallel intrinsic, at which
// point `map`/`reduce` parallelize transparently with no source changes.
//
// Self-contained: uses only core language features.

// Fork/join two pure thunks (Unit -> A) and (Unit -> B), returning both results.
let both left right = (left(Unit), right(Unit))

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
