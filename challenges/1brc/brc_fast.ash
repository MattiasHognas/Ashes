// Bespoke 1BRC trie — the fastest variant (~8.2 s at 1e9 vs ~11.5 s for brc_trie.ash), with
// byte-identical output. Same mmap / chunk / fork / merge shape as brc_trie.ash, but the
// per-worker table is a purpose-built 16-ary hash trie whose leaf stores the min/max/sum/count
// aggregate INLINE (four Ints, no value-tuple pointer) and updates it directly — the hot loop
// carries no onHit closure and boxes/unboxes no tuple. This is the "custom table with inline
// aggregates" that fast 1BRC entries use, expressed in Ashes. In-place constant memory per
// worker via the same reuse specialization that covers Ashes.Map.set / Ashes.HashTrie.
//
// Two effects, measured independently at 1e9 (interleaved against brc_trie.ash on a 9950X3D):
//   * inline stats + closure-free update      -> ~10.4 s  (safe; keeps the leaf key byte-compare)
//   * additionally trusting the 64-bit FNV hash -> ~8.2 s  (this file; no byte-compare on hit)
//
// The hash-trust step drops the leaf byte-compare and treats an equal 64-bit hash as an equal
// key. That removes one cold, scattered mmap line read per row (the stored key view points at a
// station name's first occurrence, far from the streaming cursor), which is the dominant remaining
// per-row miss once the tuple load and closure are gone. It assumes no two distinct station names
// collide in 64 bits of FNV-1a; that holds for this data set (41,343 names; collision probability
// ~5e-11) and the output is verified byte-identical. Restore the `Ashes.Bytes.compare` guard in
// `upsertStat`'s hash-match arm (chaining to `go(next)` on mismatch) for adversarial-input safety
// at the ~2 s cost above.
import Ashes.IO
import Ashes.File
import Ashes.Map
import Ashes.Text
import Ashes.String
import Ashes.Bytes
import Ashes.UInt
import Ashes.Parallel
type Trie =
    | TrieEmpty
    | TrieLeaf(Int, Str, Int, Int, Int, Int, Trie)
    | TrieNode16(Int, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie)

let empty = TrieEmpty

let recursive firstDiffShift a b shift = 
    if a >> shift & 15 == b >> shift & 15
    then firstDiffShift(a)(b)(shift + 4)
    else shift

let splitPair shift nibA a nibB b = 
    TrieNode16(shift)(if nibA == 0
    then a
    else 
        if nibB == 0
        then b
        else TrieEmpty)(if nibA == 1
    then a
    else 
        if nibB == 1
        then b
        else TrieEmpty)(if nibA == 2
    then a
    else 
        if nibB == 2
        then b
        else TrieEmpty)(if nibA == 3
    then a
    else 
        if nibB == 3
        then b
        else TrieEmpty)(if nibA == 4
    then a
    else 
        if nibB == 4
        then b
        else TrieEmpty)(if nibA == 5
    then a
    else 
        if nibB == 5
        then b
        else TrieEmpty)(if nibA == 6
    then a
    else 
        if nibB == 6
        then b
        else TrieEmpty)(if nibA == 7
    then a
    else 
        if nibB == 7
        then b
        else TrieEmpty)(if nibA == 8
    then a
    else 
        if nibB == 8
        then b
        else TrieEmpty)(if nibA == 9
    then a
    else 
        if nibB == 9
        then b
        else TrieEmpty)(if nibA == 10
    then a
    else 
        if nibB == 10
        then b
        else TrieEmpty)(if nibA == 11
    then a
    else 
        if nibB == 11
        then b
        else TrieEmpty)(if nibA == 12
    then a
    else 
        if nibB == 12
        then b
        else TrieEmpty)(if nibA == 13
    then a
    else 
        if nibB == 13
        then b
        else TrieEmpty)(if nibA == 14
    then a
    else 
        if nibB == 14
        then b
        else TrieEmpty)(if nibA == 15
    then a
    else 
        if nibB == 15
        then b
        else TrieEmpty)

let upsertStat hash key tenths = 
    (let recursive go t = 
        match t with
            | TrieEmpty -> TrieLeaf(hash)(key)(tenths)(tenths)(tenths)(1)(TrieEmpty)
            | TrieLeaf(h2, k2, mn, mx, sm, ct, next) -> 
                if h2 == hash
                then 
                    let nmn = 
                        if tenths < mn
                        then tenths
                        else mn
                    in 
                        let nmx = 
                            if tenths > mx
                            then tenths
                            else mx
                        in TrieLeaf(h2)(k2)(nmn)(nmx)(sm + tenths)(ct + 1)(next)
                else 
                    let ds = firstDiffShift(hash)(h2)(0)
                    in splitPair(ds)(hash >> ds & 15)(TrieLeaf(hash)(key)(tenths)(tenths)(tenths)(1)(TrieEmpty))(h2 >> ds & 15)(TrieLeaf(h2)(k2)(mn)(mx)(sm)(ct)(next))
            | TrieNode16(s, c0, c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, c13, c14, c15) -> 
                let nib = hash >> s & 15
                in 
                    if nib <= 7
                    then 
                        if nib <= 3
                        then 
                            if nib <= 1
                            then 
                                if nib <= 0
                                then TrieNode16(s)(go(c0))(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(go(c1))(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                            else 
                                if nib <= 2
                                then TrieNode16(s)(c0)(c1)(go(c2))(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(go(c3))(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                        else 
                            if nib <= 5
                            then 
                                if nib <= 4
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(go(c4))(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(go(c5))(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                            else 
                                if nib <= 6
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(go(c6))(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(go(c7))(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                    else 
                        if nib <= 11
                        then 
                            if nib <= 9
                            then 
                                if nib <= 8
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(go(c8))(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(go(c9))(c10)(c11)(c12)(c13)(c14)(c15)
                            else 
                                if nib <= 10
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(go(c10))(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(go(c11))(c12)(c13)(c14)(c15)
                        else 
                            if nib <= 13
                            then 
                                if nib <= 12
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(go(c12))(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(go(c13))(c14)(c15)
                            else 
                                if nib <= 14
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(go(c14))(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(go(c15))
    in go)

let upsertMerge hash key imn imx ism ict = 
    (let recursive go t = 
        match t with
            | TrieEmpty -> TrieLeaf(hash)(key)(imn)(imx)(ism)(ict)(TrieEmpty)
            | TrieLeaf(h2, k2, mn, mx, sm, ct, next) -> 
                if h2 == hash
                then 
                    let nmn = 
                        if imn < mn
                        then imn
                        else mn
                    in 
                        let nmx = 
                            if imx > mx
                            then imx
                            else mx
                        in TrieLeaf(h2)(k2)(nmn)(nmx)(sm + ism)(ct + ict)(next)
                else 
                    let ds = firstDiffShift(hash)(h2)(0)
                    in splitPair(ds)(hash >> ds & 15)(TrieLeaf(hash)(key)(imn)(imx)(ism)(ict)(TrieEmpty))(h2 >> ds & 15)(TrieLeaf(h2)(k2)(mn)(mx)(sm)(ct)(next))
            | TrieNode16(s, c0, c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, c13, c14, c15) -> 
                let nib = hash >> s & 15
                in 
                    if nib <= 7
                    then 
                        if nib <= 3
                        then 
                            if nib <= 1
                            then 
                                if nib <= 0
                                then TrieNode16(s)(go(c0))(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(go(c1))(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                            else 
                                if nib <= 2
                                then TrieNode16(s)(c0)(c1)(go(c2))(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(go(c3))(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                        else 
                            if nib <= 5
                            then 
                                if nib <= 4
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(go(c4))(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(go(c5))(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                            else 
                                if nib <= 6
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(go(c6))(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(go(c7))(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                    else 
                        if nib <= 11
                        then 
                            if nib <= 9
                            then 
                                if nib <= 8
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(go(c8))(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(go(c9))(c10)(c11)(c12)(c13)(c14)(c15)
                            else 
                                if nib <= 10
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(go(c10))(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(go(c11))(c12)(c13)(c14)(c15)
                        else 
                            if nib <= 13
                            then 
                                if nib <= 12
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(go(c12))(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(go(c13))(c14)(c15)
                            else 
                                if nib <= 14
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(go(c14))(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(go(c15))
    in go)

let foldLeft folder state = 
    (let recursive go acc t = 
        match t with
            | TrieEmpty -> acc
            | TrieLeaf(_h, key, mn, mx, sm, ct, next) -> go(folder(acc)(key)(mn)(mx)(sm)(ct))(next)
            | TrieNode16(_s, c0, c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, c13, c14, c15) -> 
                let a0 = go(acc)(c0)
                in 
                    let a1 = go(a0)(c1)
                    in 
                        let a2 = go(a1)(c2)
                        in 
                            let a3 = go(a2)(c3)
                            in 
                                let a4 = go(a3)(c4)
                                in 
                                    let a5 = go(a4)(c5)
                                    in 
                                        let a6 = go(a5)(c6)
                                        in 
                                            let a7 = go(a6)(c7)
                                            in 
                                                let a8 = go(a7)(c8)
                                                in 
                                                    let a9 = go(a8)(c9)
                                                    in 
                                                        let a10 = go(a9)(c10)
                                                        in 
                                                            let a11 = go(a10)(c11)
                                                            in 
                                                                let a12 = go(a11)(c12)
                                                                in 
                                                                    let a13 = go(a12)(c13)
                                                                    in 
                                                                        let a14 = go(a13)(c14)
                                                                        in go(a14)(c15)
    in go(state))

let toList t = 
    (let prepend rest key mn mx sm ct = (key, mn, mx, sm, ct) :: rest
    in foldLeft(prepend)([])(t))

let absInt n = 
    if n < 0
    then -n
    else n

let fmtTenths v = 
    (let sign = 
        if v < 0
        then "-"
        else ""
    in 
        let a = absInt(v)
        in 
            let whole = a / 10
            in 
                let frac = a - whole * 10
                in sign + Ashes.Text.fromInt(whole) + "." + Ashes.Text.fromInt(frac))

let fmtMean sum count = 
    (let rounded = 
        if sum >= 0
        then (sum + count / 2) / count
        else -((-sum + count / 2) / count)
    in fmtTenths(rounded))

let parseFixed bytes start stop = 
    (let len = stop - start
    in 
        if len == 3
        then Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(start)) * 10 + Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(start + 2)) - 528
        else 
            if len == 4
            then 
                if Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(start)) == 45
                then -(Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(start + 1)) * 10 + Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(start + 3)) - 528)
                else Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(start)) * 100 + Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(start + 1)) * 10 + Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(start + 3)) - 5328
            else 
                if len == 5
                then -(Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(start + 1)) * 100 + Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(start + 2)) * 10 + Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(start + 4)) - 5328)
                else 0)

let recursive foldLines bytes pos hi map = 
    if pos >= hi
    then map
    else 
        match Ashes.Bytes.scanHash(bytes)(59)(pos) with
            | (sep, nameHash) -> 
                let nlRaw = 
                    if sep < 0
                    then Ashes.Bytes.indexOf(bytes)(10)(pos)
                    else Ashes.Bytes.indexOf(bytes)(10)(sep + 1)
                in 
                    let lineEnd = 
                        if nlRaw < 0
                        then hi
                        else 
                            if nlRaw > hi
                            then hi
                            else nlRaw
                    in 
                        if sep < 0
                        then foldLines(bytes)(lineEnd + 1)(hi)(map)
                        else 
                            if sep >= lineEnd
                            then foldLines(bytes)(lineEnd + 1)(hi)(map)
                            else 
                                let name = Ashes.Bytes.subView(bytes)(pos)(sep - pos)
                                in 
                                    let tenths = parseFixed(bytes)(sep + 1)(lineEnd)
                                    in foldLines(bytes)(lineEnd + 1)(hi)(upsertStat(nameHash)(name)(tenths)(map))

let foldChunk triple = 
    match triple with
        | (bytes, lo, hi) -> foldLines(bytes)(lo)(hi)(empty)

let recursive mergeEntries entries acc = 
    match entries with
        | [] -> acc
        | (key, mn, mx, sm, ct) :: tail -> mergeEntries(tail)(upsertMerge(Ashes.Bytes.hash(Ashes.Bytes.fromText(key)))(key)(mn)(mx)(sm)(ct)(acc))

let merge a b = mergeEntries(toList(b))(a)

let recursive buildChunks bytes len lo n acc = 
    if n <= 1
    then (bytes, lo, len) :: acc
    else 
        let target = lo + (len - lo) / n
        in 
            let nl = Ashes.Bytes.indexOf(bytes)(10)(target)
            in 
                if nl < 0
                then (bytes, lo, len) :: acc
                else 
                    if nl + 1 >= len
                    then (bytes, lo, len) :: acc
                    else buildChunks(bytes)(len)(nl + 1)(n - 1)((bytes, lo, nl + 1) :: acc)

let recursive sortEntries entries acc = 
    match entries with
        | [] -> acc
        | (key, mn, mx, sm, ct) :: tail -> sortEntries(tail)(Ashes.Map.setStr(key)((mn, mx, sm, ct))(acc))

let formatEntry pair = 
    match pair with
        | (key, value) -> 
            match value with
                | (mn, mx, sm, ct) -> key + "=" + fmtTenths(mn) + "/" + fmtMean(sm)(ct) + "/" + fmtTenths(mx)

let recursive formatAll pairs acc = 
    match pairs with
        | [] -> acc
        | h :: t -> formatAll(t)(formatEntry(h) :: acc)

let run path = 
    match Ashes.File.mmap(path) with
        | Error(_e) -> "{}"
        | Ok(bytes) -> 
            let len = Ashes.Bytes.length(bytes)
            in 
                let chunks = buildChunks(bytes)(len)(0)(128)([])
                in 
                    let final = Ashes.Parallel.reduce(merge)(empty)(foldChunk)(chunks)
                    in 
                        let sorted = sortEntries(toList(final))(Ashes.Map.empty)
                        in 
                            let entries = formatAll(Ashes.Map.toList(sorted))([])
                            in "{" + Ashes.String.join(", ")(entries) + "}"
in 
    match Ashes.IO.args with
        | path :: _ -> Ashes.IO.writeLine(run(path))
        | [] -> Ashes.IO.writeLine("{}")
