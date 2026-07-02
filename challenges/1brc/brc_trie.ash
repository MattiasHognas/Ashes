// Data-parallel 1BRC over Ashes.HashTrie — the fastest variant. Same mmap/chunk/fork/merge
// shape as brc_parallel.ash, but the per-worker fold accumulates into a 16-ary hash trie
// (Ashes.HashTrie): each node carries its own nibble shift, so a row costs ~4-5 dependent
// node loads instead of the AVL's ~17 — the descent is by hash nibbles, with the full hash
// and a memcmp key compare at the leaf (equal-hash collisions chain through the leaf's next
// field). The trie is hash-ordered, so the merged result is re-sorted by station name at the
// end through Ashes.Map.setStr (41k inserts, negligible). Constant memory per worker via the
// same in-place reuse specialization that covers Map.set. The file path is the first argument.
import Ashes.IO
import Ashes.File
import Ashes.Map
import Ashes.Text
import Ashes.String
import Ashes.Bytes
import Ashes.UInt
import Ashes.Parallel
import Ashes.HashTrie
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

let rec parseTenthsBytes bytes i stop sign acc = 
    if i >= stop
    then sign * acc
    else 
        let b = Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(i))
        in 
            if b == 45
            then parseTenthsBytes(bytes)(i + 1)(stop)(-1)(acc)
            else 
                if b == 46
                then parseTenthsBytes(bytes)(i + 1)(stop)(sign)(acc)
                else parseTenthsBytes(bytes)(i + 1)(stop)(sign)(acc * 10 + b - 48)

let updateStats existing tenths = 
    match existing with
        | (mn, mx, sm, ct) -> 
            let newMin = 
                if tenths < mn
                then tenths
                else mn
            in 
                let newMax = 
                    if tenths > mx
                    then tenths
                    else mx
                in (newMin, newMax, sm + tenths, ct + 1)

let rec foldLines bytes pos hi map = 
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
                                    let tenths = parseTenthsBytes(bytes)(sep + 1)(lineEnd)(1)(0)
                                    in 
                                        foldLines(bytes)(lineEnd + 1)(hi)(Ashes.HashTrie.upsertHashed(nameHash)(name)((tenths, tenths, tenths, 1))(fun (old) -> updateStats(old)(tenths))(map))

let foldChunk triple = 
    match triple with
        | (bytes, lo, hi) -> foldLines(bytes)(lo)(hi)(Ashes.HashTrie.empty)

let mergeValues a b = 
    match a with
        | (mnA, mxA, smA, ctA) -> 
            match b with
                | (mnB, mxB, smB, ctB) -> 
                    let mn = 
                        if mnB < mnA
                        then mnB
                        else mnA
                    in 
                        let mx = 
                            if mxB > mxA
                            then mxB
                            else mxA
                        in (mn, mx, smA + smB, ctA + ctB)

let mergeStation acc key value = 
    Ashes.HashTrie.upsertHashed(Ashes.HashTrie.hashText(key))(key)(value)(fun (existing) -> mergeValues(existing)(value))(acc)

let rec mergeEntries entries acc = 
    match entries with
        | [] -> acc
        | (key, value) :: tail -> mergeEntries(tail)(mergeStation(acc)(key)(value))

let merge a b = mergeEntries(Ashes.HashTrie.toList(b))(a)

let rec buildChunks bytes len lo n acc = 
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

let formatEntry pair = 
    match pair with
        | (key, value) -> 
            match value with
                | (mn, mx, sm, ct) -> key + "=" + fmtTenths(mn) + "/" + fmtMean(sm)(ct) + "/" + fmtTenths(mx)

let rec formatAll pairs acc = 
    match pairs with
        | [] -> acc
        | h :: t -> formatAll(t)(formatEntry(h) :: acc)

let rec sortEntries entries acc = 
    match entries with
        | [] -> acc
        | (key, value) :: tail -> sortEntries(tail)(Ashes.Map.setStr(key)(value)(acc))

let run path = 
    match Ashes.File.mmap(path) with
        | Error(_e) -> "{}"
        | Ok(bytes) -> 
            let len = Ashes.Bytes.length(bytes)
            in 
                let chunks = buildChunks(bytes)(len)(0)(32)([])
                in 
                    let final = Ashes.Parallel.reduce(merge)(Ashes.HashTrie.empty)(foldChunk)(chunks)
                    in 
                        let sorted = sortEntries(Ashes.HashTrie.toList(final))(Ashes.Map.empty)
                        in 
                            let entries = formatAll(Ashes.Map.toList(sorted))([])
                            in "{" + Ashes.String.join(", ")(entries) + "}"
in 
    match Ashes.IO.args with
        | path :: _ -> Ashes.IO.writeLine(run(path))
        | [] -> Ashes.IO.writeLine("{}")
