// expect: 50005000
// Lookup-then-update fold: each step reads the recursive-ADT accumulator (uget, a tail-recursive
// tree lookup that matches but never rebuilds) and then rebuilds it (uset). Before the defensive-copy
// fix, uget's TCO recursion was (wrongly) given an in-place-reuse defensive deep copy of its subtree
// argument even though it only reuses a dead nullary leaf (Lf -> None), making every lookup O(size)
// and the whole fold O(N^2) (a user map fold OOM'd ~50-100k rows). The fix keeps that copy only when
// reuse rebuilds non-nullary structure. Sum of all values is N*(N+1)/2 regardless of tree shape, so
// this checks correctness while exercising the pattern at a size that was multi-GB before the fix.
type T(A) =
    | Lf
    | Br(T, A, A, T)

let recursive uget m k =
    match m with
        | Lf -> None
        | Br(l, key, v, r) ->
            if k == key
            then Some(v)
            else
                if k <= key
                then uget(l)(k)
                else uget(r)(k)

let recursive uset m k v =
    match m with
        | Lf -> Br(Lf)(k)(v)(Lf)
        | Br(l, key, ov, r) ->
            if k == key
            then Br(l)(k)(v)(r)
            else
                if k <= key
                then Br(uset(l)(k)(v))(key)(ov)(r)
                else Br(l)(key)(ov)(uset(r)(k)(v))

let recursive usum m =
    match m with
        | Lf -> 0
        | Br(l, _k, v, r) -> v + usum(l) + usum(r)

let key i = i * 48271 & 8191

let addRow m i =
    (let k = key(i)
    in
        match uget(m)(k) with
            | None -> uset(m)(k)(i)
            | Some(prev) -> uset(m)(k)(prev + i))

let recursive loop i m =
    if i <= 0
    then m
    else loop(i - 1)(addRow(m)(i))

Ashes.IO.print(Ashes.Text.fromInt(usum(loop(10000)(Lf))))
