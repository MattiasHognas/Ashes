// Data-parallel 1BRC. Maps the whole file into a zero-copy Bytes view with Ashes.File.mmap (no read or
// copy; the mapping is shared read-only across worker threads and its pages fault in in parallel), then
// splits it into per-core (bytes, lo, hi) chunks at newline boundaries,
// folds each chunk into a partial station Map on a worker thread (Ashes.Parallel.reduce forks via
// `both` at the concrete Map result type), and merges the partial Maps. The result is identical to the
// sequential fold (purity makes it order-independent). Per row the fold takes a zero-copy
// Ashes.Bytes.subView of the station name (the mmap backing is program-lifetime, and Map storage
// materializes keys into the persistent blob) and threads it through upsertMeasurement — a
// user-defined single-traversal min/max/sum/count upsert in the Ashes.Map.set shape, which the
// compiler reuse-specializes to rewrite the accumulator tree in place at constant memory per worker.
// The worker cap defaults to the machine's core count (detected at program start; override with
// --parallel-workers). foldChunk is a top-level, non-capturing function (bytes travels through the
// chunk tuple, never a closure capture) so nothing arena-allocated crosses a fork. The file path is
// the first argument.
import Ashes.IO
import Ashes.File
import Ashes.Map
import Ashes.Map.MapTree
import Ashes.Text
import Ashes.String
import Ashes.Bytes
import Ashes.UInt
import Ashes.Parallel
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

let recursive parseTenthsBytes bytes i stop sign acc = 
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

let upsertMeasurement newKey tenths = 
    (let recursive go map = 
        match map with
            | Empty -> Ashes.Map.makeNode(Empty)(newKey)((tenths, tenths, tenths, 1))(Empty)
            | Node(_height, left, key, value, right) -> 
                let ordering = Ashes.Bytes.compare(Ashes.Bytes.fromText(newKey))(Ashes.Bytes.fromText(key))
                in 
                    if ordering == 0
                    then 
                        match value with
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
                                    in Ashes.Map.makeNode(left)(key)((newMin, newMax, sm + tenths, ct + 1))(right)
                    else 
                        if ordering <= -1
                        then Ashes.Map.balance(Ashes.Map.makeNode(go(left))(key)(value)(right))
                        else Ashes.Map.balance(Ashes.Map.makeNode(left)(key)(value)(go(right)))
    in go)

let recursive foldLines bytes pos hi map = 
    if pos >= hi
    then map
    else 
        let nlRaw = Ashes.Bytes.indexOf(bytes)(10)(pos)
        in 
            let lineEnd = 
                if nlRaw < 0
                then hi
                else 
                    if nlRaw > hi
                    then hi
                    else nlRaw
            in 
                let sep = Ashes.Bytes.indexOf(bytes)(59)(pos)
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
                                in foldLines(bytes)(lineEnd + 1)(hi)(upsertMeasurement(name)(tenths)(map))

let foldChunk triple = 
    match triple with
        | (bytes, lo, hi) -> foldLines(bytes)(lo)(hi)(Ashes.Map.empty)

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
    Ashes.Map.upsertStr(key)(value)(given (existing) -> mergeValues(existing)(value))(acc)

let merge a b = Ashes.Map.foldLeft(mergeStation)(a)(b)

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
                let chunks = buildChunks(bytes)(len)(0)(32)([])
                in 
                    let final = Ashes.Parallel.reduce(merge)(Ashes.Map.empty)(foldChunk)(chunks)
                    in 
                        let entries = formatAll(Ashes.Map.toList(final))([])
                        in "{" + Ashes.String.join(", ")(entries) + "}"
in 
    match Ashes.IO.args with
        | path :: _ -> Ashes.IO.writeLine(run(path))
        | [] -> Ashes.IO.writeLine("{}")
