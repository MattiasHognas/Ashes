// expect: 16|8
// Data-parallel chunked fold: a Bytes is split into per-core (bytes, lo, hi) chunk tuples, each folded
// on a worker thread into a partial Map (Ashes.Parallel.reduce forks via `both` at the concrete Map
// result type), and the partial Maps are merged. The workers read the shared Bytes and the merged
// result is deep-copied across the fork. Result is identical to a sequential fold (purity). Here each
// chunk counts ';' (59) and 'a' (97) in its byte range into a two-key Map; the 32-byte input has 16
// semicolons and 8 'a's, split across four chunks.
import Ashes.IO
import Ashes.Text
import Ashes.Map
import Ashes.String
import Ashes.Bytes
import Ashes.UInt
import Ashes.Parallel
let recursive countInto bytes i hi map =
    if i >= hi
    then map
    else
        let b = Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(i))
        in
            let key =
                if b == 59
                then "semi"
                else "a"
            in
                let bump =
                    if b == 59
                    then true
                    else b == 97
                in
                    if bump
                    then
                        let map2 =
                            match Ashes.Map.get(Ashes.String.compare)(key)(map) with
                                | None -> Ashes.Map.set(Ashes.String.compare)(key)(1)(map)
                                | Some(c) -> Ashes.Map.set(Ashes.String.compare)(key)(c + 1)(map)
                        in countInto(bytes)(i + 1)(hi)(map2)
                    else countInto(bytes)(i + 1)(hi)(map)

let foldChunk triple =
    match triple with
        | (bytes, lo, hi) -> countInto(bytes)(lo)(hi)(Ashes.Map.empty)

let mergeOne acc key value =
    match Ashes.Map.get(Ashes.String.compare)(key)(acc) with
        | None -> Ashes.Map.set(Ashes.String.compare)(key)(value)(acc)
        | Some(e) -> Ashes.Map.set(Ashes.String.compare)(key)(e + value)(acc)

let merge a b = Ashes.Map.foldLeft(mergeOne)(a)(b)

let bytes = Ashes.Bytes.fromText("a;a;a;a;a;a;a;a;b;c;d;e;f;g;h;;z")

let chunks = (bytes, 0, 8) :: (bytes, 8, 16) :: (bytes, 16, 24) :: (bytes, 24, 32) :: []

let total = Ashes.Parallel.reduce(merge)(Ashes.Map.empty)(foldChunk)(chunks)

let semi =
    match Ashes.Map.get(Ashes.String.compare)("semi")(total) with
        | Some(v) -> v
        | None -> 0

let acount =
    match Ashes.Map.get(Ashes.String.compare)("a")(total) with
        | Some(v) -> v
        | None -> 0
in Ashes.IO.print(Ashes.Text.fromInt(semi) + "|" + Ashes.Text.fromInt(acount))
