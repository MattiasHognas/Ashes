// Structured, deterministic parallelism over pure functions. The result of every
// operation here is IDENTICAL to its sequential equivalent — purity guarantees
// order-independence — so code written with these combinators is always correct.
//
// `both` (the single fork/join primitive) is a compiler intrinsic registered in
// BuiltinRegistry — it must be lowered at each call site so it can deep-copy a worker's
// result at the concrete result type — so it is NOT defined here. At concrete result types
// `both` runs its right thunk on a worker thread; polymorphic uses fall back to sequential
// evaluation (always correct).
//
// `mapGrained`/`reduceGrained` are divide-and-conquer with a grain threshold: at or below
// `grain` elements they run sequentially (plSeqMap/plSeqReduce), and above it they split the
// list in half and evaluate the two halves through `both`. A saturated call at a concrete
// element type is monomorphized by the compiler, so `both` sees a concrete result and
// genuinely forks; used polymorphically (or partially applied) they degrade to a sequential —
// but still correct — evaluation. `map`/`reduce` are the grain-1 defaults. The list helpers are
// top-level so the monomorphic specialization references them as static code (never an arena
// closure that could cross a fork).
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

let rec plSeqMap f xs = 
    match xs with
        | [] -> []
        | h :: t -> f(h) :: plSeqMap(f)(t)

let rec plSeqReduce combine identity f xs = 
    match xs with
        | [] -> identity
        | h :: [] -> f(h)
        | h :: t -> combine(f(h))(plSeqReduce(combine)(identity)(f)(t))

let rec mapGrained grain f xs = 
    match xs with
        | [] -> []
        | h :: [] -> f(h) :: []
        | _ -> 
            if plLength(xs) <= grain
            then plSeqMap(f)(xs)
            else 
                let half = plLength(xs) / 2
                in 
                    match Ashes.Parallel.both(fun (_u) -> mapGrained(grain)(f)(plTake(xs)(half)))(fun (_u) -> mapGrained(grain)(f)(plDrop(xs)(half))) with
                        | (leftRes, rightRes) -> plAppend(leftRes)(rightRes)

let rec reduceGrained grain combine identity f xs = 
    match xs with
        | [] -> identity
        | h :: [] -> f(h)
        | _ -> 
            if plLength(xs) <= grain
            then plSeqReduce(combine)(identity)(f)(xs)
            else 
                let half = plLength(xs) / 2
                in 
                    match Ashes.Parallel.both(fun (_u) -> reduceGrained(grain)(combine)(identity)(f)(plTake(xs)(half)))(fun (_u) -> reduceGrained(grain)(combine)(identity)(f)(plDrop(xs)(half))) with
                        | (leftRes, rightRes) -> combine(leftRes)(rightRes)

let map f xs = mapGrained(1)(f)(xs)

let reduce combine identity f xs = reduceGrained(1)(combine)(identity)(f)(xs)
