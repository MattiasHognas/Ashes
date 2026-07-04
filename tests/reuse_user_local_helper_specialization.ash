// expect: 50|124999750000|500000
// CO-22: a user fold in the entry file whose rebuild helpers are its OWN local functions (not
// Ashes.Map.makeNode) is now reuse-specialized to constant memory. The entry-body registration
// computes a transitive free-variable closure: upd is registerable because its only non-stitched
// free var is mkNode, which is registerable because its only free var is hgt (registerable: its free
// vars are constructors), so all three register (upd + hgt/mkNode inlinable) and upd$reuse folds the
// helpers into in-place-reuse constructors. Before CO-22 the fold referenced a user sibling helper and
// was rejected outright and leaked unboundedly (1.48 GB at 1M inserts). Inserts 500k rows into a
// bounded 50-key space (each key updated on hit) and reads back every key: totalSum = n(n-1)/2 and
// totalCt = n are shape-independent invariants, so a reuse use-after-free would change them.
import Ashes.IO
import Ashes.Map
import Ashes.Map.MapTree
import Ashes.Text
import Ashes.String
let hgt t = 
    match t with
        | Empty -> 0
        | Node(h, _l, _k, _v, _r) -> h

let mkNode l key value r = 
    (let hl = hgt(l)
    in 
        let hr = hgt(r)
        in 
            let h = 
                if hl > hr
                then hl + 1
                else hr + 1
            in Node(h)(l)(key)(value)(r))

let upd newKey tenths = 
    (let recursive go map = 
        match map with
            | Empty -> mkNode(Empty)(newKey)((tenths, tenths, tenths, 1))(Empty)
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
                                    in mkNode(left)(key)((newMin, newMax, sm + tenths, ct + 1))(right)
                    else 
                        if ordering <= -1
                        then mkNode(go(left))(key)(value)(right)
                        else mkNode(left)(key)(value)(go(right))
    in go)

let recursive loop i n map = 
    if i >= n
    then map
    else 
        let key = "k" + Ashes.Text.fromInt(i - i / 50 * 50)
        in loop(i + 1)(n)(upd(key)(i)(map))

let recursive readAll j m sumAcc ctAcc = 
    if j >= 50
    then (sumAcc, ctAcc)
    else 
        match Ashes.Map.getStr("k" + Ashes.Text.fromInt(j))(m) with
            | Some((_mn, _mx, sm, ct)) -> readAll(j + 1)(m)(sumAcc + sm)(ctAcc + ct)
            | None -> readAll(j + 1)(m)(sumAcc)(ctAcc)

let n = 500000

let final = loop(0)(n)(Empty)
in 
    match readAll(0)(final)(0)(0) with
        | (totalSum, totalCt) -> Ashes.IO.print(Ashes.Text.fromInt(Ashes.Map.size(final)) + "|" + Ashes.Text.fromInt(totalSum) + "|" + Ashes.Text.fromInt(totalCt))
