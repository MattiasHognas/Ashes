// A user-defined nested-recursive-return fold (the Map.set shape, written in the entry file) must be
// reuse-specialized like a stdlib one: constant memory across many iterations, with an in-arm
// computed tuple value materialized correctly (min/max/sum update on hit).
// expect: k17=(17,80017,17,1)
import Ashes.IO
import Ashes.Map
import Ashes.Map.MapTree
import Ashes.Text
import Ashes.String
let upd newKey tenths =
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

let recursive loop i map =
    if i >= 80020
    then map
    else
        let key = "k" + Ashes.Text.fromInt(i - i / 20 * 20)
        in loop(i + 1)(upd(key)(i)(map))

let final = loop(17)(Empty)

let shown =
    match Ashes.Map.getStr("k17")(final) with
        | Some((mn, mx, sm, ct)) -> "k17=(" + Ashes.Text.fromInt(mn) + "," + Ashes.Text.fromInt(mx) + "," + Ashes.Text.fromInt(sm - sm / 1000 * 1000) + "," + Ashes.Text.fromInt(ct - ct / 1000 * 1000) + ")"
        | None -> "missing"

Ashes.IO.writeLine(shown)
