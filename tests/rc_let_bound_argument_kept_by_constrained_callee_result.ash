// expect: alpha__v
// A `let`-bound reference-counted value handed to a callee whose own result keeps it must survive
// the `let` scope's exit. `setAssoc` compares keys with `==`, so it takes a hidden `Eq` dictionary
// and its call is lowered by the trait-evidence path rather than the ordinary call path — which is
// where the call-boundary retain used to be missing. The first call's `key` was released at its
// scope exit while the returned table still held it, and the second call's key reused the freed
// cell, so looking the first entry back up answered with the wrong one.
//
// `Node` exists only to make `Box` self-reaching. Without it the scope copies its result out at the
// `let`'s exit and the copy carries a fresh key, which hides the defect: a self-recursive result
// type has no copy-out kind, so the scope abandons its window and nothing rescues the released key.
type Node =
    | label: Str
    | children: List(Node)

type Box =
    | entries: List((Str, Maybe(Str)))
    | nodes: List(Node)

let recursive lookupAssoc k es =
    match es with
        | [] -> None
        | (a, b) :: tail ->
            if a == k
            then Some(b)
            else lookupAssoc(k)(tail)

let recursive setAssoc k v es =
    match es with
        | [] -> [(k, v)]
        | (a, b) :: tail ->
            if a == k
            then (k, v) :: tail
            else (a, b) :: setAssoc(k)(v)(tail)

let variantKey (label: Str) (n: Int) = label + "#" + Ashes.Text.fromInt(n)

let getOrCreate (label: Str) (n: Int) (box: Box) =
    (let key = variantKey(label)(n)
    in
        match lookupAssoc(key)(box.entries) with
            | Some(memo) -> (memo, box)
            | None -> (Some(label + "__v"), (box with entries = setAssoc(key)(Some(label + "__v"))(box.entries), nodes = Node(label = label, children = []) :: box.nodes)))

let describe (m: Maybe(Str)) =
    match m with
        | Some(value) -> value
        | None -> "<none>"

Ashes.IO.print(match getOrCreate("alpha")(1)(Box(entries = [], nodes = [])) with
    | (_r1, b1) ->
        match getOrCreate("beta")(1)(b1) with
            | (_r2, b2) ->
                match lookupAssoc("alpha#1")(b2.entries) with
                    | Some(v) -> describe(v)
                    | None -> "<alpha entry lost>")
